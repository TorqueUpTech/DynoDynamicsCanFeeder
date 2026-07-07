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
    /// A sensible starting point. Includes BOTH the (working) weather-station data points AND the
    /// EDM sensor inputs, so a fresh open never drops one for the other.
    ///
    /// Weather: published as WEATHER_STATION DATA_VALUE (Param 1/2/3, physical value via Scale).
    /// EDM: air-temp + 3 custom inputs as raw ADC counts (0..4096); air-temp default is the captured
    /// live value 600 on param 25 (0x19) flags 1 (≈18 C via the matching custom-channel cal).
    ///
    /// EDM source device = EDM: physically the sensors are on the EDM. NB the handheld captures
    /// showed these DATA_VALUE frames actually broadcast by HANDHELD(4) (the EDM(3) only sent a
    /// FW_CTRL heartbeat), so if DigitalDyno doesn't pick a channel up, switch that row's Device to
    /// HANDHELD. The custom channel matches on the param (SourceId), which is likely device-agnostic.
    /// </summary>
    public static CommsEmulatorConfig Default()
    {
        return new CommsEmulatorConfig
        {
            DataPoints =
            {
                // Weather station (was working in DigitalDyno) - physical value, scaled.
                new CommsDataPoint { Name = "Weather Air Temp", Device = "WEATHER_STATION", Cmd = 15, Param = 1, Value = 25.0,   Scale = 0.1 },
                new CommsDataPoint { Name = "Baro Pressure",    Device = "WEATHER_STATION", Cmd = 15, Param = 2, Value = 1013.0, Scale = 0.1 },
                new CommsDataPoint { Name = "Humidity",         Device = "WEATHER_STATION", Cmd = 15, Param = 3, Value = 50.0,   Scale = 0.1 },
                // EDM sensor inputs - confirmed from a live FTDI capture (Ftd2xx.dll proxy) decoded
                // with reverse/ftdi-logger. EDM broadcasts DATA_VALUE with flags 0; raw value, the
                // built-in dyno channel applies the scale. Inlet Air Temp = param 0x24 (36), raw/10 = C
                // (verified: raw 175 -> DigitalDyno showed 17.5 C). The others are the EDM's other
                // connected inputs (label/recalibrate as your 3 custom sensors).
                new CommsDataPoint { Name = "EDM Air Temp", Device = "EDM", Cmd = 15, Param = 36, Flags = 0, Value = 175,  Scale = 1 }, // 0x24, built-in inlet temp
                // EDM Inputs 1-4 = the general-purpose analog ports (params 0x20/0x28/0x3A/0x3B),
                // idle by default. Set a value here + a matching custom channel (SourceId = param).
                new CommsDataPoint { Name = "EDM Input 1", Device = "EDM", Cmd = 15, Param = 32, Flags = 0, Value = 1061, Scale = 1 }, // 0x20
                new CommsDataPoint { Name = "EDM Input 2", Device = "EDM", Cmd = 15, Param = 40, Flags = 0, Value = 5000, Scale = 1 }, // 0x28
                new CommsDataPoint { Name = "EDM Input 3", Device = "EDM", Cmd = 15, Param = 58, Flags = 0, Value = 1215, Scale = 1 }, // 0x3A
                new CommsDataPoint { Name = "EDM Input 4", Device = "EDM", Cmd = 15, Param = 59, Flags = 0, Value = 27,   Scale = 1 }, // 0x3B
            }
        };
    }
}
