using System.Text.Json;

namespace DynoDynamicsCanFeeder;

/// <summary>
/// One value the comms emulator publishes as a device (e.g. weather air-temp). The dyno reads
/// <c>Value</c> after the emulator sends the raw u16 = round((Value - Offset) / Scale) in a
/// DynoCAN packet sourced from <see cref="Device"/> with command <see cref="Cmd"/> and field
/// <see cref="Param"/>. Param/Scale/Offset are best-guess defaults — tune against a live capture.
/// </summary>
public sealed class CommsDataPoint
{
    public string Name { get; set; } = "";
    /// <summary>Source device id (eDeviceId name), e.g. "WEATHER_STATION".</summary>
    public string Device { get; set; } = "WEATHER_STATION";
    /// <summary>DynoCAN command byte (eNewCmd), 15 = DATA_VALUE.</summary>
    public byte Cmd { get; set; } = 15;
    /// <summary>Field id within the device (data[0]).</summary>
    public byte Param { get; set; }
    /// <summary>Flags/sub byte (data[1]). EDM sensor frames carry 0x01 here; 0 otherwise.</summary>
    public byte Flags { get; set; }
    public double Value { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }
}

/// <summary>
/// Config for the comms-hub emulator: which DynoServer to reach, which devices announce themselves
/// as present, how often, and the data points to publish.
/// </summary>
public sealed class CommsEmulatorConfig
{
    /// <summary>DynoServer address. Empty => LAN broadcast discovery; "loopback" => 127.0.0.1.</summary>
    public string ServerAddress { get; set; } = "";

    /// <summary>How often (ms) to re-announce presence + republish data points.</summary>
    public int IntervalMs { get; set; } = 500;

    /// <summary>Device ids (eDeviceId names) to announce as present via DEVICE_STATUS.</summary>
    public List<string> PresentDevices { get; set; } = new()
    {
        "COMMS_BOX", "WEATHER_STATION", "PIT_MASTER", "EDM", "HANDHELD"
    };

    public List<CommsDataPoint> DataPoints { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static CommsEmulatorConfig Load(string path) =>
        JsonSerializer.Deserialize<CommsEmulatorConfig>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidOperationException($"Empty/invalid config: {path}");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));

    /// <summary>
    /// A sensible starting point for EDM sensor emulation: present the common devices and feed the
    /// EDM air-temp input plus 3 custom inputs as raw ADC counts (0..4096). The air-temp default is
    /// the captured live value 600 on HANDHELD param 25 (0x19) with flags 1 (≈18 C via the matching
    /// custom-channel cal). Values are raw counts (Scale 1); tune them + the DB channel cal to taste.
    /// </summary>
    public static CommsEmulatorConfig Default()
    {
        return new CommsEmulatorConfig
        {
            DataPoints =
            {
                new CommsDataPoint { Name = "Air Temp", Device = "HANDHELD", Cmd = 15, Param = 25, Flags = 1, Value = 600,  Scale = 1 },
                new CommsDataPoint { Name = "Custom 1", Device = "HANDHELD", Cmd = 15, Param = 27, Flags = 1, Value = 2048, Scale = 1 },
                new CommsDataPoint { Name = "Custom 2", Device = "HANDHELD", Cmd = 15, Param = 28, Flags = 1, Value = 2048, Scale = 1 },
                new CommsDataPoint { Name = "Custom 3", Device = "HANDHELD", Cmd = 15, Param = 29, Flags = 1, Value = 2048, Scale = 1 },
            }
        };
    }
}
