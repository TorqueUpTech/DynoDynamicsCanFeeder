using System.Diagnostics;
using System.Net;
using System.Threading;
using DynoDynamics.Connection;
using DynoDynamics.DynoServer;
using DynoDynamics.IO;

namespace DynoDynamicsCanFeeder;

/// <summary>
/// Emulates comms-hub devices to DigitalDyno. Registers with DynoServer as a producer and, on a
/// timer, (a) announces each configured device as present via a DEVICE_STATUS packet sourced from
/// that <see cref="eDeviceId"/>, and (b) republishes each data point (e.g. weather air-temp) as a
/// DATA_VALUE packet — the same DynoCAN transport the ECU feeder uses, just with device/status
/// commands instead of ECU channels.
///
/// EXPERIMENTAL: the transport is correct, but the exact presence handshake and per-field
/// Param/scaling that DigitalDyno accepts live in native code + device firmware and need tuning
/// against a live capture. Everything semantic is therefore configurable.
/// </summary>
public sealed class CommsEmulator : IDisposable
{
    // DynoCAN command bytes (eNewCmd) we emit.
    private const byte CMD_DATA_VALUE = 15;
    private const byte CMD_DEVICE_STATUS = 37;

    private readonly CommsEmulatorConfig _cfg;
    private readonly ProducerConnection _conn;
    private readonly Stopwatch _clock = new();
    private Thread? _serverThread;
    private Timer? _timer;
    private volatile bool _registered;

    /// <summary>Human-readable status/log lines (may fire on background threads).</summary>
    public event Action<string>? Status;

    public CommsEmulator(CommsEmulatorConfig cfg)
    {
        _cfg = cfg;
        // Register as a Weather Station producer (a real device type); the per-packet SourceId still
        // lets us speak for COMMS_BOX / PIT_MASTER / EDM as well.
        _conn = new ProducerConnection("COMMS", eProducerType.WeatherStation, eDynoGeneration.Digital);
        _conn.onRegister = (status, _) =>
        {
            _registered = status == IOStatus.Succeeded;
            Status?.Invoke($"[server] register: {status}");
        };
        _conn.onConnect = (status, _) => Status?.Invoke($"[server] connect: {status}");
        _conn.onError   = (ex, _)     => Status?.Invoke($"[server] error: {ex.Message}");
        _conn.onClose   = (status, _) => Status?.Invoke($"[server] close: {status}");
    }

    public void Start()
    {
        // Hardcoded to 127.0.0.1 — same as the ECU feeder: DynoServer is local/loopback-bound and
        // broadcast discovery was leaking out the OBDX WiFi interface. (cfg.ServerAddress is ignored.)
        var addr = IPAddress.Loopback;
        Status?.Invoke($"[server] target {addr} (hardcoded loopback)");
        _clock.Restart();

        _serverThread = new Thread(() => _conn.ConnectAndRunWithRetries(addr, this))
        {
            IsBackground = true, Name = "CommsEmu-Server"
        };
        _serverThread.Start();

        int period = Math.Max(50, _cfg.IntervalMs);
        _timer = new Timer(_ => Tick(), null, 200, period);
        Status?.Invoke($"[comms] emulating {_cfg.PresentDevices.Count} device(s), " +
                       $"{_cfg.DataPoints.Count} data point(s) every {period} ms");
    }

    private void Tick()
    {
        if (!_registered) return;
        try
        {
            ushort ts = (ushort)((long)_clock.Elapsed.TotalMilliseconds & 0xFFFF);

            // (a) presence — one DEVICE_STATUS per configured device.
            foreach (string name in _cfg.PresentDevices)
            {
                if (!TryDevice(name, out eDeviceId dev)) continue;
                var p = MakePacket(dev, CMD_DEVICE_STATUS, ts);
                p.Param = 1;              // best-guess "OK/present" status
                p.ValueUnsigned = 0;
                _conn.Send(p);
            }

            // (b) data points — DATA_VALUE (or configured cmd) per point.
            foreach (var dp in _cfg.DataPoints)
            {
                if (!TryDevice(dp.Device, out eDeviceId dev)) continue;
                var p = MakePacket(dev, dp.Cmd, ts);
                p.Param = dp.Param;
                p.Sub = dp.Flags;   // data[1] — EDM sensor frames set this to 0x01
                p.ValueUnsigned = ToRaw(dp.Value, dp.Scale, dp.Offset);
                _conn.Send(p);
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke("[comms] send error: " + ex.Message);
        }
    }

    private static DynoCAN_Packet MakePacket(eDeviceId source, byte cmd, ushort ts)
    {
        var p = new DynoCAN_Packet
        {
            SourceId = source,
            DestId = eDeviceId.PC,
            cmd = cmd
        };
        p.Timestamp = ts;
        return p;
    }

    private static ushort ToRaw(double value, double scale, double offset)
    {
        if (scale == 0) scale = 1;
        long raw = (long)Math.Round((value - offset) / scale);
        if (raw < 0) raw = 0;
        if (raw > ushort.MaxValue) raw = ushort.MaxValue;
        return (ushort)raw;
    }

    private bool TryDevice(string name, out eDeviceId dev)
    {
        if (Enum.TryParse(name, ignoreCase: true, out dev)) return true;
        Status?.Invoke($"[comms] unknown device id '{name}' — skipped");
        return false;
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        try { _conn.Stop(); } catch { /* best effort */ }
        _registered = false;
        Status?.Invoke("[comms] stopped");
    }

    public void Dispose() => Stop();
}
