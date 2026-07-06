using DbcParserLib;
using DbcParserLib.Model;

// Validates the authored DBCs by decoding synthetic frames with the SAME call the feeder uses
// (Packer.RxSignalUnpack). Proven Motorola convention: big-endian value in bytes N..N+1 -> start N*8+7.

// Locate the repo's DBC/ folder by walking up from the build output (portable after a clone).
string dir = FindDbcDir();
static string FindDbcDir()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        string p = Path.Combine(d.FullName, "DBC");
        if (Directory.Exists(p)) return p;
    }
    return AppContext.BaseDirectory;
}
int fails = 0;

double Decode(string dbc, string msgName, string sigName, byte[] data)
{
    Dbc d = Parser.ParseFromPath(Path.Combine(dir, dbc));
    Message msg = d.Messages.First(m => m.Name == msgName);
    Signal sig = msg.Signals.First(s => s.Name == sigName);
    return Packer.RxSignalUnpack(data, sig);
}

void Check(string label, double got, double expect, double tol)
{
    bool ok = Math.Abs(got - expect) <= tol;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}: got {got:0.#####}, expect {expect:0.#####}");
    if (!ok) fails++;
}

static byte[] Frame(params byte[] b) { var d = new byte[8]; Array.Copy(b, d, b.Length); return d; }

Console.WriteLine("Haltech_WB2.dbc (corrected big-endian @0):");
Check("Engine_RPM=6000", Decode("Haltech_WB2.dbc", "Haltech_360", "Engine_RPM",
    Frame(0x17, 0x70)), 6000, 0.5);
Check("Wideband_Sensor_1=0.900", Decode("Haltech_WB2.dbc", "Haltech_368", "Wideband_Sensor_1",
    Frame(0x03, 0x84)), 0.900, 0.001);
Check("Wideband_Overall=0.850", Decode("Haltech_WB2.dbc", "Haltech_470", "Wideband_Overall",
    Frame(0x03, 0x52)), 0.850, 0.001);

Console.WriteLine("AEM_X-Series.dbc (big-endian @0, ID 0x180):");
Check("Lambda=1.0000", Decode("AEM_X-Series.dbc", "AEM_XSeries_Gauge1", "Lambda",
    Frame(0x27, 0x10, 0, 0, 0, 0, 0x80, 0)), 1.0000, 0.0002);
Check("Lambda=0.8500", Decode("AEM_X-Series.dbc", "AEM_XSeries_Gauge1", "Lambda",
    Frame(0x21, 0x34)), 0.8500, 0.0002);
Check("System_Volts=12.5", Decode("AEM_X-Series.dbc", "AEM_XSeries_Gauge1", "System_Volts",
    Frame(0, 0, 0, 0, 125)), 12.5, 0.05);
Check("Lambda_Data_Valid=1", Decode("AEM_X-Series.dbc", "AEM_XSeries_Gauge1", "Lambda_Data_Valid",
    Frame(0x27, 0x10, 0, 0, 0, 0, 0x80, 0)), 1, 0.0);
Check("Sensor_Fault=1", Decode("AEM_X-Series.dbc", "AEM_XSeries_Gauge1", "Sensor_Fault",
    Frame(0, 0, 0, 0, 0, 0, 0, 0x40)), 1, 0.0);

Console.WriteLine("ECOtrons_ALM.dbc (little-endian @1, ID 0x0CFF0001):");
Check("Lambda~1.0", Decode("ECOtrons_ALM.dbc", "ALM_Ch1Frame1", "Lambda",
    Frame(0, 0, 0x02, 0x10)), 4098 * 0.000244, 0.0005);
Check("O2_Percent@raw0=-12", Decode("ECOtrons_ALM.dbc", "ALM_Ch1Frame1", "O2_Percent",
    Frame(0, 0)), -12.0, 0.001);
Check("Temp~25C", Decode("ECOtrons_ALM.dbc", "ALM_Ch1Frame1", "Sensor_Temperature",
    Frame(0, 0, 0, 0, 0xAA, 0x31)), 12714 * 0.023438 - 273, 0.05);
Check("Sensor_Faults=10", Decode("ECOtrons_ALM.dbc", "ALM_Ch1Frame1", "Sensor_Faults",
    Frame(0, 0, 0, 0, 0, 0, 0x0A, 0)), 10, 0.0);

// ECM_HS.dbc is a vendor GM DBC, intentionally NOT shipped in this repo. Drop your own copy
// into DBC/ to exercise this check; otherwise it is skipped (not a failure).
if (File.Exists(Path.Combine(dir, "ECM_HS.dbc")))
{
    Console.WriteLine("ECM_HS.dbc (GM, vendor DBC as-is, big-endian, 500k):");
    // EngSpd 6000 rpm -> raw 24000 = 0x5DC0, start 15 (byte1 MSB) -> data[1]=0x5D, data[2]=0xC0
    Check("EngSpd=6000", Decode("ECM_HS.dbc", "PPEI_Engine_General_Status_1", "EngSpd",
        Frame(0, 0x5D, 0xC0)), 6000, 0.25);
}
else Console.WriteLine("ECM_HS.dbc: SKIPPED (vendor DBC not present in DBC/).");

Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILURE(S)");
return fails == 0 ? 0 : 1;
