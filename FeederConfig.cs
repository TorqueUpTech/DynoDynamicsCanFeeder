using System.Text.Json;
using System.Text.Json.Serialization;
using IxxatCanTool.Can;

namespace DynoDynamicsCanFeeder;

/// <summary>One DBC signal routed to a DigitalDyno ECU channel.</summary>
public sealed class SignalRoute
{
    /// <summary>DBC signal name to read (e.g. "Engine_RPM", "Wideband_Overall").</summary>
    public string Signal { get; set; } = "";

    /// <summary>Target sys_ecuchanneldef.ChannelNo the dyno displays this on.</summary>
    public int ChannelNo { get; set; }

    /// <summary>Scale/Offset the dyno's channel def uses. We send raw = round((phys-Offset)/Scale).</summary>
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }

    /// <summary>Informational: true if this is the RPM/tacho channel (sys_ecu.TachoChannel).</summary>
    public bool IsTacho { get; set; }
}

public sealed class FeederConfig
{
    /// <summary>"IxxatVci" or "Obdx".</summary>
    public string Adapter { get; set; } = "IxxatVci";

    /// <summary>Device key from enumeration; empty = first device found.</summary>
    public string DeviceKey { get; set; } = "";

    /// <summary>"Br1000kBit", "Br500kBit", "Br250kBit", ...</summary>
    public string BitRate { get; set; } = "Br500kBit";

    /// <summary>Path to the DBC used to decode this source.</summary>
    public string DbcPath { get; set; } = "";

    /// <summary>DynoServer address. Empty => LAN broadcast discovery (the stock DynoClient*
    /// default; works on this rig). Use "loopback" to force 127.0.0.1 (same-host), or a literal
    /// IP for a remote server. Previously this field was ignored - it is now honoured.</summary>
    public string ServerAddress { get; set; } = "";

    /// <summary>
    /// ECU presence-heartbeat period, in milliseconds. The feeder emits a DEVICE_STATUS packet from
    /// eDeviceId.ECU_INTERFACE at this rate so DigitalDyno keeps the "Device ECU" module present even
    /// when the source CAN bus is quiet (engine off / wideband not broadcasting) — decoupled from the
    /// actual signal data, which is still forwarded only when real frames arrive. Must be safely below
    /// the dyno's ~5 s comms watchdog. 0 disables it (legacy behaviour: ECU present only while data flows).
    /// </summary>
    public int HeartbeatMs { get; set; } = 1000;

    /// <summary>
    /// Fallback: if the dyno's ECU comms watchdog turns out to require channel DATA_VALUE traffic
    /// (not just DEVICE_STATUS), set true so the heartbeat ALSO re-sends each route's last-known raw
    /// value. Default false = presence-only, so no stale readings are ever re-transmitted.
    /// </summary>
    public bool HeartbeatResendChannels { get; set; }

    /// <summary>
    /// Restrict the CAN adapter to only the message IDs that carry routed signals (the IDs the dyno
    /// actually shows), instead of listening promiscuously to the whole bus. Prevents a bandwidth-
    /// limited link (e.g. OBDX over WiFi) from overloading its buffer. Only adapters that support
    /// hardware acceptance filtering honour it (OBDX); others receive everything regardless. Default
    /// on; if no routed IDs resolve from the DBC, the filter is skipped (receive all) so you never
    /// silently get nothing.
    /// </summary>
    public bool FilterToRoutes { get; set; } = true;

    public List<SignalRoute> Routes { get; set; } = new();

    public CanAdapterKind AdapterKind =>
        Enum.TryParse<CanAdapterKind>(Adapter, ignoreCase: true, out var k) ? k : CanAdapterKind.IxxatVci;

    public CanBitRate BitRateValue =>
        Enum.TryParse<CanBitRate>(BitRate, ignoreCase: true, out var b) ? b : CanBitRate.Br500kBit;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static FeederConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<FeederConfig>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidOperationException($"Empty/invalid config: {path}");
        // Resolve a relative DbcPath against the config file's own folder (not the process CWD),
        // so a config that ships next to its DBC/ folder works no matter where it's launched from.
        if (!string.IsNullOrWhiteSpace(cfg.DbcPath) && !Path.IsPathRooted(cfg.DbcPath))
        {
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            string resolved = Path.GetFullPath(Path.Combine(baseDir, cfg.DbcPath));
            if (File.Exists(resolved)) cfg.DbcPath = resolved;
        }
        return cfg;
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));

    /// <summary>All signal names in a DBC, for populating the GUI route picker. Empty on error.</summary>
    public static IReadOnlyList<string> ReadDbcSignalNames(string dbcPath)
        => ReadDbcSignals(dbcPath).Select(s => s.Name).ToList();

    /// <summary>
    /// DBC signal metadata used to pre-fill a route: the physical Scale (Factor) and Offset the
    /// decoder applies, the bit length (1-bit = a status flag, not a measurement), and the unit.
    /// Defaulting a route's Scale/Offset to these makes the feeder pass the raw CAN value through
    /// and lets the dyno re-apply the same scaling (an exact round-trip once the matching channel
    /// def is created via "Import from feeder routes").
    /// </summary>
    public readonly record struct DbcSignalInfo(string Name, double Factor, double Offset, int Length, string Unit);

    public static IReadOnlyList<DbcSignalInfo> ReadDbcSignals(string dbcPath)
    {
        try
        {
            var dbc = DbcParserLib.Parser.ParseFromPath(dbcPath);
            return dbc.Messages
                .SelectMany(m => m.Signals)
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(s => new DbcSignalInfo(s.Name, s.Factor, s.Offset, (int)s.Length, s.Unit ?? ""))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<DbcSignalInfo>(); }
    }
}
