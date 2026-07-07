using System.Net;
using DynoDynamics.Connection;
using DynoDynamics.DynoServer;
using DynoDynamics.ECU;
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
    private readonly ECU_Packet _pkt = new();   // reused; FrameReceived is single-threaded
    private ICanAdapter? _adapter;
    private Thread? _serverThread;

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
        _conn.onRegister = (status, s) => Status?.Invoke($"[server] register: {status}");
        _conn.onError    = (ex, s)     => Status?.Invoke($"[server] error: {ex.Message}");
        _conn.onClose    = (status, s) => Status?.Invoke($"[server] close: {status}");
    }

    public void Start()
    {
        // Which address to reach DynoServer on. Default (blank) = LAN broadcast discovery, exactly
        // like the stock DynoClient* modules (their logs show "Server Discover (Succeeded)" then a
        // connect on the machine's LAN IP that registers fine). An explicit ServerAddress overrides
        // - e.g. "loopback"/"127.0.0.1" to force same-host, or a literal IP for a remote server.
        var serverAddress = ResolveServerAddress(_cfg.ServerAddress);
        Status?.Invoke($"[server] target {serverAddress}");
        _serverThread = new Thread(() => _conn.ConnectAndRunWithRetries(serverAddress, this))
        {
            IsBackground = true, Name = "DynoServer"
        };
        _serverThread.Start();

        _adapter = AdapterFactory.Create(_cfg.AdapterKind);
        _adapter.FrameReceived += OnFrame;
        _adapter.BusError += msg => Status?.Invoke($"[can] {msg}");

        var device = ResolveDevice(_cfg);
        Status?.Invoke($"[can] {_cfg.AdapterKind} @ {_cfg.BitRateValue} on {device.Description}");
        _adapter.Connect(device, _cfg.BitRateValue, listenOnly: true);
        Status?.Invoke("[can] connected — listening");
    }

    /// <summary>
    /// Resolve the DynoServer address. Blank/"broadcast"/"any" => LAN broadcast discovery (the
    /// stock DynoClient* default). "loopback"/"localhost" => 127.0.0.1 (force same-host). Any
    /// other value is parsed as a literal IP; unparseable falls back to broadcast.
    /// </summary>
    private static IPAddress ResolveServerAddress(string? configured)
    {
        var s = configured?.Trim();
        if (string.IsNullOrEmpty(s) ||
            s!.Equals("broadcast", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("any", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Broadcast;
        if (s.Equals("loopback", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        return IPAddress.TryParse(s, out var ip) ? ip : IPAddress.Broadcast;
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

            _pkt.SetEcuChannel(route.ChannelNo);
            _pkt.SetDataValue(raw);
            _pkt.SetTimestamp((ushort)Environment.TickCount);
            _conn.Send(_pkt);

            SignalDecoded?.Invoke(sig.Signal, sig.Value);
        }
    }

    public void Dispose()
    {
        try { _adapter?.Disconnect(); } catch { /* best effort */ }
        try { _conn.Stop(); } catch { /* best effort */ }
        _adapter?.Dispose();
    }
}
