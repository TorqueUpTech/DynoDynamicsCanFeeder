using DynoDynamicsCanFeeder;

// Exercises the real EcuDatabase against a COPY of dyno-e.db (never the live file):
// list units/ecus, create an ECU + channels, read back, update (replace not duplicate), delete.

int fails = 0;
void Check(string label, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}"); if (!ok) fails++; }

string? src = args.Length > 0 ? args[0] : EcuDatabase.AutoLocate();
if (src is null || !File.Exists(src)) { Console.Error.WriteLine("dyno-e.db not found."); return 2; }
string tmp = Path.Combine(Path.GetTempPath(), "dyno-e.selftest.db");
File.Copy(src, tmp, overwrite: true);
Console.WriteLine($"Testing against copy of {src}");

try
{
    var db = new EcuDatabase(tmp);

    var units = db.ListUnits();
    Check($"ListUnits ({units.Count})", units.Count > 0 && units.Any(u => u.UnitId == 161));

    var ecus0 = db.ListEcus();
    Check($"ListEcus baseline ({ecus0.Count})", ecus0.Count > 0);

    int pid = db.SuggestProtocolId();
    Check($"SuggestProtocolId ({pid})", pid >= 20 && ecus0.All(e => e.ProtocolId != pid));

    var ecu = new EcuInfo { Name = "FEEDER_SELFTEST", Description = "selftest", TachoChannel = 1, ProtocolId = pid };
    var chans = new List<ChannelDef>
    {
        new() { ChannelNo = 1,  ChannelName = "RPM",    ShortName = "RPM",    UnitId = 161, UnitName = "rpm",    Scale = 1.0,   Offset = 0, DataType = 2 },
        new() { ChannelNo = 10, ChannelName = "Lambda", ShortName = "Lambda", UnitId = 280, UnitName = "Lambda", Scale = 0.001, Offset = 0, DataType = 2 },
    };
    db.SaveEcu(ecu, chans);
    Check("SaveEcu assigned row id", ecu.Id is > 0);

    var back = db.ListChannels(pid);
    Check($"Read back 2 channels ({back.Count})", back.Count == 2);
    Check("Lambda scale 0.001 persisted", back.Any(c => c.ChannelNo == 10 && Math.Abs(c.Scale - 0.001) < 1e-9 && c.UnitId == 280));
    Check("Tacho RPM channel persisted", back.Any(c => c.ChannelNo == 1 && c.UnitId == 161));
    Check("ECU now listed", db.ListEcus().Any(e => e.ProtocolId == pid && e.Name == "FEEDER_SELFTEST"));

    // Update: change a scale, save again -> must REPLACE (still 2 rows), not duplicate.
    chans[1].Scale = 0.0005;
    ecu.Description = "updated";
    db.SaveEcu(ecu, chans);
    var back2 = db.ListChannels(pid);
    Check($"After update still 2 channels ({back2.Count})", back2.Count == 2);
    Check("Updated scale persisted", back2.Any(c => c.ChannelNo == 10 && Math.Abs(c.Scale - 0.0005) < 1e-9));
    Check("ECU not duplicated", db.ListEcus().Count(e => e.ProtocolId == pid) == 1);

    // Delete.
    db.DeleteEcu(ecu);
    Check("Channels gone after delete", db.ListChannels(pid).Count == 0);
    Check("ECU gone after delete", db.ListEcus().All(e => e.ProtocolId != pid));

    // Original untouched.
    Check("Live DB untouched (baseline count unchanged)", new EcuDatabase(src).ListEcus().Count == ecus0.Count);
}
catch (Exception ex) { Console.Error.WriteLine("EXCEPTION: " + ex); fails++; }
finally { try { File.Delete(tmp); } catch { } }

Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAILURE(S)");
return fails == 0 ? 0 : 1;
