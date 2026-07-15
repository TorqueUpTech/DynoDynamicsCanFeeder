using System.Net;
using DynoDynamics.Connection;
using DynoDynamics.DynoServer;
using DynoDynamics.ECU;
using DynoDynamics.IO;
using IxxatCanTool.Can;
using IxxatCanTool.Decoding;

namespace DynoDynamicsCanFeeder;

/// <summary>
/// Bridges a live CAN bus (IXXAT / OBDX, via the UltimateCANScanner adapters) into
/// Dyno Dynamics DigitalDyno. Decodes user-chosen DBC signals (RPM, lambda, ...) and
/// republishes them to DynoServer as an "ECU" producer — exactly as DynoClientECU.exe
/// does, but sourced from any DBC-described device instead of a hard-coded protocol.
///
/// UI-agnostic: raises <see cref="Status"/> / <see cref="SignalDecoded"/> events (on
/// background threads) so either the WinForms GUI or the headless CLI can consume it.
/// </summary>
public sealed class Feeder : IDisposable
{
    private readonly FeederConfig _cfg;
    private readonly DbcDecoder _decoder;
    private readonly Dictionary<string, SignalRoute> _routes;
    private readonly ProducerConnection _conn;
    private readonly ECU_Packet _pkt = new();          // channel data; only sent under _sendLock
    private readonly object _sendLock = new();         // serialises RX-thread and heartbeat sends
    private readonly Dictionary<int, ushort> _lastRaw = new();  // ChannelNo -> last raw (keepalive)
    private ICanAdapter? _adapter;
    private Thread? _serverThread;
    private Thread? _canThread;                         // connects the CAN adapter, with retries
    private System.Threading.Timer? _heartbeat;
    private volatile bool _registered;
    private volatile bool _stopping;

    private const byte CmdDeviceStatus = 37;           // eNewCmd.DEVICE_STATUS

    /// <summary>Human-readable status/log line (connection, errors, lifecycle).</summary>
    public event Action<string>? Status;

    /// <summary>A routed signal's decoded physical value, for live display (signal name, value).</summary>
    public event Action<string, double>? SignalDecoded;

    public Feeder(FeederConfig cfg)
    {
        _cfg = cfg;
        _decoder = DbcDecoder.Load(cfg.DbcPath);
        _routes = cfg.Routes.ToDictionary(r => r.Signal, StringComparer.OrdinalIgnoreCase);

        _conn = new ProducerConnection("ECU", eProducerType.oldECU, eDynoGeneration.Digital);
        _conn.onConnect  = (status, s) => Status?.Invoke($"[server] connect: {status}");
        _conn.onRegister = (status, s) =>
        {
            _registered = status == IOStatus.Succeeded;
            Status?.Invoke($"[server] register: {status}");
        };
        _conn.onError    = (ex, s)     => Status?.Invoke($"[server] error: {ex.Message}");
        _conn.onClose    = (status, s) => Status?.Invoke($"[server] close: {status}");
    }

    public void Start()
    {
        // Hardcoded to 127.0.0.1. DynoServer runs on THIS PC bound to loopback; broadcast discovery
        // was leaking out the OBDX WiFi interface (192.168.4.x) and either failing to register or
        // attaching to the wrong server. Loopback is unambiguous and always correct for this rig.
        // (cfg.ServerAddress is intentionally ignored.)
        var serverAddress = IPAddress.Loopback;
        Status?.Invoke($"[server] target {serverAddress} (hardcoded loopback)");
        _serverThread = new Thread(() => _conn.ConnectAndRunWithRetries(serverAddress, this))
        {
            IsBackground = true, Name = "DynoServer"
        };
        _serverThread.Start();

        // Presence heartbeat: keep the dyno's "Device ECU" module alive independent of source data.
        // Sends a DEVICE_STATUS from ECU_INTERFACE every HeartbeatMs (gated on registration). Without
        // this the ECU only counts as present while CAN frames flow, so a quiet bus trips the dyno's
        // ~5 s comms watchdog ("Device ECU Comms failed") and the module drops out.
        if (_cfg.HeartbeatMs > 0)
        {
            int period = Math.Max(100, _cfg.HeartbeatMs);
            _heartbeat = new System.Threading.Timer(_ => Heartbeat(), null, 250, period);
            Status?.Invoke($"[ecu] presence heartbeat every {period} ms" +
                           (_cfg.HeartbeatResendChannels ? " (+channel keepalive)" : ""));
        }

        _adapter = AdapterFactory.Create(_cfg.AdapterKind);
        _adapter.FrameReceived += OnFrame;
        _adapter.BusError += msg => Status?.Invoke($"[can] {msg}");

        // Restrict the adapter to just the IDs that carry routed signals, so a bandwidth-limited link
        // (OBDX over WiFi) isn't flooded with the whole bus. Only adapters that hardware-filter honour
        // this (OBDX); others receive everything (fine on local USB). Skip if nothing resolves, so a
        // DBC/route mismatch never silently starves the feed.
        if (_cfg.FilterToRoutes && _adapter is ICanAcceptanceFilter filterable)
        {
            var ids = _decoder.MessageIdsForSignals(_routes.Keys)
                              .Select(m => new CanAcceptId(m.Id, m.Extended))
                              .ToList();
            if (ids.Count > 0)
            {
                filterable.SetAcceptanceFilter(ids);
                Status?.Invoke($"[can] filtering to {ids.Count} routed ID(s): " +
                    string.Join(", ", ids.Select(i => (i.Extended ? "x" : "") + i.Id.ToString("X"))));
            }
            else
            {
                Status?.Invoke("[can] no routed IDs resolved from DBC — receiving all (filter skipped)");
            }
        }

        // Connect the CAN adapter on its own retrying thread. The server link + presence heartbeat are
        // already up, so a down/flaky source (OBDX WiFi not ready, no device yet) NEVER stops the feeder
        // or the ECU heartbeat — the module stays present and data just starts flowing when the bus is
        // reachable. (Once connected, the adapter's own RX loop + auto-reconnect handle later drops.)
        _canThread = new Thread(ConnectCanLoop) { IsBackground = true, Name = "CanConnect" };
        _canThread.Start();
    }

    private void ConnectCanLoop()
    {
        while (!_stopping)
        {
            try
            {
                var device = ResolveDevice(_cfg);
                Status?.Invoke($"[can] {_cfg.AdapterKind} @ {_cfg.BitRateValue} on {device.Description}");
                _adapter!.Connect(device, _cfg.BitRateValue, listenOnly: true);
                Status?.Invoke("[can] connected — listening");
                return; // connected; the adapter self-manages reconnects from here
            }
            catch (Exception ex)
            {
                if (_stopping) return;
                Status?.Invoke($"[can] connect failed: {ex.Message} — retrying in 5 s (ECU stays present)");
                for (int i = 0; i < 50 && !_stopping; i++)
                    Thread.Sleep(100);
            }
        }
    }

    private CanDeviceInfo ResolveDevice(FeederConfig cfg)
    {
        // OBDX and GVRET endpoints (tcp:host:port, serial:COMx, ble:...) are addressable, not
        // enumerable, so honour an explicit transport key directly — this is how a WiFi endpoint on
        // a non-default IP (or a GVRET board on TCP) is selected.
        bool addressable = cfg.AdapterKind is CanAdapterKind.Obdx or CanAdapterKind.Gvret or CanAdapterKind.VirtualTcp;
        if (addressable && LooksLikeTransportKey(cfg.DeviceKey))
            return new CanDeviceInfo(cfg.AdapterKind, cfg.DeviceKey, $"{cfg.AdapterKind} (configured)", cfg.DeviceKey);

        var devices = AdapterFactory.Enumerate(cfg.AdapterKind);
        if (devices.Count == 0)
            throw new InvalidOperationException($"No {cfg.AdapterKind} devices found.");
        if (string.IsNullOrEmpty(cfg.DeviceKey))
            return devices[0];
        foreach (var d in devices)
            if (d.Key == cfg.DeviceKey) return d;
        throw new InvalidOperationException($"Device key '{cfg.DeviceKey}' not present.");
    }

    private static bool LooksLikeTransportKey(string key) =>
        key.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("serial:", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("ble:", StringComparison.OrdinalIgnoreCase);

    /// <summary>RX-thread callback: decode the frame and forward routed signals to the dyno.</summary>
    private void OnFrame(CanFrame frame)
    {
        IReadOnlyList<DecodedSignal> signals;
        try { signals = _decoder.DecodeSignals(frame); }
        catch { return; } // frame not in this DBC / undecodable — ignore

        foreach (var sig in signals)
        {
            if (!_routes.TryGetValue(sig.Signal, out var route))
                continue;

            // The dyno multiplies our raw u16 by the channel's DB Scale (+Offset), so invert here.
            double rawD = Math.Round((sig.Value - route.Offset) / route.Scale);
            ushort raw = (ushort)Math.Max(ushort.MinValue, Math.Min(ushort.MaxValue, rawD));

            lock (_sendLock)
            {
                _lastRaw[route.ChannelNo] = raw;   // remembered for the optional keepalive
                SendChannel(route.ChannelNo, raw);
            }

            SignalDecoded?.Invoke(sig.Signal, sig.Value);
        }
    }

    /// <summary>Send one ECU channel value. Caller must hold <see cref="_sendLock"/> (shared _pkt).</summary>
    private void SendChannel(int channelNo, ushort raw)
    {
        _pkt.SetEcuChannel(channelNo);
        _pkt.SetDataValue(raw);
        _pkt.SetTimestamp((ushort)Environment.TickCount);
        _conn.Send(_pkt);
    }

    /// <summary>
    /// Timer callback: announce the ECU as present via a DEVICE_STATUS packet (independent of the
    /// source bus). Optionally re-sends last-known channel values if the dyno's watchdog needs them.
    /// </summary>
    private void Heartbeat()
    {
        if (!_registered) return;
        try
        {
            lock (_sendLock)
            {
                var status = new DynoCAN_Packet
                {
                    SourceId = eDeviceId.ECU_INTERFACE,
                    DestId   = eDeviceId.PC,
                    cmd      = CmdDeviceStatus,
                };
                status.Param = 1;            // "present / OK"
                status.ValueUnsigned = 0;
                status.Timestamp = (ushort)Environment.TickCount;
                _conn.Send(status);

                if (_cfg.HeartbeatResendChannels)
                    foreach (var kv in _lastRaw)
                        SendChannel(kv.Key, kv.Value);
            }
        }
        catch (Exception ex)
        {
            Status?.Invoke("[ecu] heartbeat error: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _stopping = true; // stops the CAN connect-retry loop
        try { _heartbeat?.Dispose(); } catch { /* best effort */ }
        try { _adapter?.Disconnect(); } catch { /* best effort */ }
        try { _conn.Stop(); } catch { /* best effort */ }
        _adapter?.Dispose();
    }
}
