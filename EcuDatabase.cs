using System.Data.SQLite;
using System.Globalization;

namespace DynoDynamicsCanFeeder;

/// <summary>One row of DigitalDyno's sys_ecu (a selectable ECU / wideband source).</summary>
public sealed class EcuInfo
{
    public long? Id;               // sys_ecu.ID (null = new, not yet inserted)
    public string Name = "";
    public string Description = "";
    public int TachoChannel;       // ChannelNo that carries RPM (0 = none)
    public int ProtocolId;         // selects the channel-def set
    public string ActiveChans = "";
    public override string ToString() => $"[{ProtocolId}] {Name}";
}

/// <summary>One row of sys_ecuchanneldef (how a channel value is labelled/scaled).</summary>
public sealed class ChannelDef
{
    public int ChannelNo { get; set; }
    public string ChannelName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public int UnitId { get; set; }
    public string UnitName { get; set; } = "";
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; }
    public int DataType { get; set; } = 2;   // 16-bit; the feeder sends a ushort
}

/// <summary>A sys_unit row, for the unit picker.</summary>
public sealed class UnitInfo
{
    public int UnitId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Label => (string.IsNullOrEmpty(Name) ? Description : Name) + $"  (#{UnitId})";
    public override string ToString() => Label;
}

/// <summary>
/// A DigitalDyno custom sensor channel (user_customchanneldef) — e.g. an EDM analog input
/// calibrated to a real unit. <see cref="SourceId"/> is the decimal of the wire DATA_VALUE param
/// byte the device sends (EDM input 0x19 -> 25). Calibration is 2-point linear from volts
/// (<see cref="CalIn0"/>V -> <see cref="CalOut0"/>unit, <see cref="CalIn1"/>V -> <see cref="CalOut1"/>unit).
/// </summary>
public sealed class CustomChannel
{
    public long? ChannelId;               // null = new row
    public bool IsActive { get; set; } = true;
    public string ChannelName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public int UnitId { get; set; }        // display unit (70/80 = C, 91 = kPa, 200 = V)
    public int SourceId { get; set; }      // device input = decimal of the DATA_VALUE param byte
    public double CalIn0 { get; set; }
    public double CalOut0 { get; set; }
    public double CalIn1 { get; set; } = 5;
    public double CalOut1 { get; set; } = 5;
    public override string ToString() => $"{ChannelName}  (src {SourceId})";
}

/// <summary>
/// Read/write access to DigitalDyno's ECU database (dyno-e.db): list ECUs, their channel
/// definitions and units, and upsert a feeder ECU + its channels in one transaction (after a backup).
/// </summary>
public sealed class EcuDatabase
{
    public string Path { get; }

    public EcuDatabase(string path) => Path = path;

    private SQLiteConnection Open()
    {
        var c = new SQLiteConnection($"Data Source={Path};Version=3;");
        c.Open();
        return c;
    }

    /// <summary>
    /// Best guess at the database DigitalDyno actually reads/writes at runtime.
    ///
    /// DYNO_Main.exe hardcodes its data root as <c>C:\DynoData</c> and opens a per-dyno-type file
    /// there: 2WD -> <c>dyno-2.db</c>, 4WD -> <c>dyno-4.db</c>, plus <c>dyno-e/-m/-qt</c> variants.
    /// The <c>...\Documents\&lt;ver&gt;\databases\dyno-*.db</c> set is only the installer's master
    /// template (seed/restore source) and is NOT written by the running app - editing it has no
    /// effect on the live dropdown. So prefer the live <c>C:\DynoData</c> copy, and among the
    /// <c>dyno-*.db</c> there pick the most-recently-written one (the app touches its active DB,
    /// e.g. on ActiveChans update / shutdown). Fall back to the Documents template only if the
    /// live folder is absent.
    /// </summary>
    public static string? AutoLocate() => ListCandidateDatabases().FirstOrDefault();

    /// <summary>
    /// All plausible dyno databases, best-first: live <c>C:\DynoData\dyno-*.db</c> (newest write
    /// first), then the Documents template set. Lets the editor default sensibly and still offer a
    /// pick-list. Never throws.
    /// </summary>
    public static IReadOnlyList<string> ListCandidateDatabases()
    {
        var found = new List<string>();
        void AddFrom(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir, "dyno-*.db", SearchOption.AllDirectories)
                                           .OrderByDescending(File.GetLastWriteTimeUtc))
                    if (!found.Contains(f, StringComparer.OrdinalIgnoreCase))
                        found.Add(f);
            }
            catch { /* unreadable dir - skip */ }
        }

        // 1) The live data root DYNO_Main uses.
        AddFrom(@"C:\DynoData");
        try { AddFrom(System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "DynoData")); } catch { }
        // 2) Installer master/template copies under the user's Documents.
        try { AddFrom(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)); } catch { }
        return found;
    }

    public List<UnitInfo> ListUnits()
    {
        var list = new List<UnitInfo>();
        using var c = Open();
        using var cmd = new SQLiteCommand("SELECT UnitId, Name, Description FROM sys_unit ORDER BY Description", c);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UnitInfo { UnitId = r.GetInt32(0), Name = r.IsDBNull(1) ? "" : r.GetString(1), Description = r.IsDBNull(2) ? "" : r.GetString(2) });
        return list;
    }

    public List<EcuInfo> ListEcus()
    {
        var list = new List<EcuInfo>();
        using var c = Open();
        using var cmd = new SQLiteCommand("SELECT ID, Name, Description, TachoChannel, ProtocolID, ActiveChans FROM sys_ecu ORDER BY ProtocolID", c);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EcuInfo
            {
                Id = r.GetInt64(0),
                Name = r.IsDBNull(1) ? "" : r.GetString(1),
                Description = r.IsDBNull(2) ? "" : r.GetString(2),
                TachoChannel = r.GetInt32(3),
                ProtocolId = r.GetInt32(4),
                ActiveChans = r.IsDBNull(5) ? "" : r.GetString(5),
            });
        return list;
    }

    public List<ChannelDef> ListChannels(int protocolId)
    {
        var list = new List<ChannelDef>();
        using var c = Open();
        using var cmd = new SQLiteCommand(
            "SELECT ChannelNo, ChannelName, ShortName, UnitID, UnitName, Scale, Offset, DataType " +
            "FROM sys_ecuchanneldef WHERE ProtocolID=@p ORDER BY ChannelNo", c);
        cmd.Parameters.AddWithValue("@p", protocolId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ChannelDef
            {
                ChannelNo = r.GetInt32(0),
                ChannelName = r.IsDBNull(1) ? "" : r.GetString(1),
                ShortName = r.IsDBNull(2) ? "" : r.GetString(2),
                UnitId = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                UnitName = r.IsDBNull(4) ? "" : r.GetString(4),
                Scale = r.IsDBNull(5) ? 0 : r.GetDouble(5),
                Offset = r.IsDBNull(6) ? 0 : r.GetDouble(6),
                DataType = r.IsDBNull(7) ? 1 : r.GetInt32(7),
            });
        return list;
    }

    /// <summary>Lowest free ProtocolID at or above 20 (keeps clear of the vendor 1..14 set).</summary>
    public int SuggestProtocolId()
    {
        using var c = Open();
        using var cmd = new SQLiteCommand("SELECT DISTINCT ProtocolID FROM sys_ecu", c);
        var used = new HashSet<int>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) used.Add(r.GetInt32(0));
        int id = 20;
        while (used.Contains(id)) id++;
        return id;
    }

    /// <summary>Copy the DB alongside itself with a timestamp; returns the backup path.</summary>
    public string Backup()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string bak = Path + $".bak-{stamp}";
        File.Copy(Path, bak, overwrite: false);
        return bak;
    }

    /// <summary>
    /// Insert/update the ECU row and replace all its channel defs, in one transaction.
    /// Caller should <see cref="Backup"/> first. Returns the ECU row ID.
    /// </summary>
    public long SaveEcu(EcuInfo ecu, IReadOnlyList<ChannelDef> channels)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        if (ecu.Id is { } id)
        {
            using var up = new SQLiteCommand(
                "UPDATE sys_ecu SET Name=@n, Description=@d, TachoChannel=@t, ProtocolID=@p, ActiveChans=@a WHERE ID=@id", c, tx);
            Bind(up, ecu); up.Parameters.AddWithValue("@id", id);
            up.ExecuteNonQuery();
        }
        else
        {
            using var ins = new SQLiteCommand(
                "INSERT INTO sys_ecu (Name, Description, TachoChannel, ProtocolID, ActiveChans) VALUES (@n,@d,@t,@p,@a)", c, tx);
            Bind(ins, ecu); ins.ExecuteNonQuery();
            ecu.Id = c.LastInsertRowId;
        }

        using (var del = new SQLiteCommand("DELETE FROM sys_ecuchanneldef WHERE ProtocolID=@p", c, tx))
        {
            del.Parameters.AddWithValue("@p", ecu.ProtocolId);
            del.ExecuteNonQuery();
        }

        foreach (var ch in channels.OrderBy(x => x.ChannelNo))
        {
            using var ins = new SQLiteCommand(
                "INSERT INTO sys_ecuchanneldef (ProtocolID, ChannelNo, ChannelName, ShortName, Description, UnitID, UnitName, Scale, Offset, DataType) " +
                "VALUES (@p,@no,@cn,@sn,@desc,@uid,@un,@sc,@off,@dt)", c, tx);
            ins.Parameters.AddWithValue("@p", ecu.ProtocolId);
            ins.Parameters.AddWithValue("@no", ch.ChannelNo);
            ins.Parameters.AddWithValue("@cn", ch.ChannelName ?? "");
            ins.Parameters.AddWithValue("@sn", string.IsNullOrEmpty(ch.ShortName) ? ch.ChannelName ?? "" : ch.ShortName);
            ins.Parameters.AddWithValue("@desc", "");
            ins.Parameters.AddWithValue("@uid", ch.UnitId);
            ins.Parameters.AddWithValue("@un", ch.UnitName ?? "");
            ins.Parameters.AddWithValue("@sc", ch.Scale);
            ins.Parameters.AddWithValue("@off", ch.Offset);
            ins.Parameters.AddWithValue("@dt", ch.DataType);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
        return ecu.Id!.Value;
    }

    public void DeleteEcu(EcuInfo ecu)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var d1 = new SQLiteCommand("DELETE FROM sys_ecuchanneldef WHERE ProtocolID=@p", c, tx))
        { d1.Parameters.AddWithValue("@p", ecu.ProtocolId); d1.ExecuteNonQuery(); }
        if (ecu.Id is { } id)
            using (var d2 = new SQLiteCommand("DELETE FROM sys_ecu WHERE ID=@id", c, tx))
            { d2.Parameters.AddWithValue("@id", id); d2.ExecuteNonQuery(); }
        tx.Commit();
    }

    private static void Bind(SQLiteCommand cmd, EcuInfo e)
    {
        cmd.Parameters.AddWithValue("@n", e.Name ?? "");
        cmd.Parameters.AddWithValue("@d", e.Description ?? "");
        cmd.Parameters.AddWithValue("@t", e.TachoChannel);
        cmd.Parameters.AddWithValue("@p", e.ProtocolId);
        cmd.Parameters.AddWithValue("@a", e.ActiveChans ?? "");
    }

    // ---- Custom sensor channels (user_customchanneldef) ----

    /// <summary>List the non-deleted custom channels (EDM inputs, etc.).</summary>
    public List<CustomChannel> ListCustomChannels()
    {
        var list = new List<CustomChannel>();
        using var c = Open();
        using var cmd = new SQLiteCommand(
            "SELECT ChannelId, IsActive, ChannelName, ShortName, UnitId, SourceId, CalibParams " +
            "FROM user_customchanneldef WHERE IsDeleted=0 ORDER BY ChannelId", c);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var cc = new CustomChannel
            {
                ChannelId = r.GetInt64(0),
                IsActive = r.GetInt32(1) != 0,
                ChannelName = r.IsDBNull(2) ? "" : r.GetString(2),
                ShortName = r.IsDBNull(3) ? "" : r.GetString(3),
                UnitId = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                SourceId = r.IsDBNull(5) ? 0 : r.GetInt32(5),
            };
            ParseCal(r.IsDBNull(6) ? "" : r.GetString(6), cc);
            list.Add(cc);
        }
        return list;
    }

    private static void ParseCal(string s, CustomChannel cc)
    {
        var p = s.Split(';');
        double D(int i) => p.Length > i && double.TryParse(p[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (p.Length >= 4) { cc.CalIn0 = D(0); cc.CalOut0 = D(1); cc.CalIn1 = D(2); cc.CalOut1 = D(3); }
    }

    /// <summary>
    /// Insert/update a custom channel (2-point linear cal from volts to its unit). Mirrors the
    /// fixed fields DigitalDyno's own EDM rows use (InputType 0 = straight voltage, CalibType 1,
    /// CalibFromUnit 200 = V). Caller should <see cref="Backup"/> first. Returns the row id.
    /// </summary>
    public long SaveCustomChannel(CustomChannel cc)
    {
        string cal = string.Format(CultureInfo.InvariantCulture, "{0};{1};{2};{3}", cc.CalIn0, cc.CalOut0, cc.CalIn1, cc.CalOut1);
        string shortName = string.IsNullOrEmpty(cc.ShortName) ? cc.ChannelName : cc.ShortName;
        using var c = Open();
        if (cc.ChannelId is { } id)
        {
            using var up = new SQLiteCommand(
                "UPDATE user_customchanneldef SET IsActive=@act, ChannelName=@cn, ShortName=@sn, UnitId=@uid, " +
                "SourceId=@src, CalibParams=@cal, CalibToUnit=@uid WHERE ChannelId=@id", c);
            BindCustom(up, cc, cal, shortName);
            up.Parameters.AddWithValue("@id", id);
            up.ExecuteNonQuery();
            return id;
        }
        using var ins = new SQLiteCommand(
            "INSERT INTO user_customchanneldef " +
            "(IsActive, IsDefault, IsDeleted, IsLocked, ChannelName, ShortName, UnitId, SourceId, SensorName, " +
            " InputType, InputParams, FilterDef, CalibType, CalibParams, CalibFromUnit, CalibToUnit, Comments) " +
            "VALUES (@act,0,0,0,@cn,@sn,@uid,@src,'Custom',0,'','N',1,@cal,200,@uid,'')", c);
        BindCustom(ins, cc, cal, shortName);
        ins.ExecuteNonQuery();
        cc.ChannelId = c.LastInsertRowId;
        return cc.ChannelId.Value;
    }

    private static void BindCustom(SQLiteCommand cmd, CustomChannel cc, string cal, string shortName)
    {
        cmd.Parameters.AddWithValue("@act", cc.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@cn", cc.ChannelName ?? "");
        cmd.Parameters.AddWithValue("@sn", shortName ?? "");
        cmd.Parameters.AddWithValue("@uid", cc.UnitId);
        cmd.Parameters.AddWithValue("@src", cc.SourceId);
        cmd.Parameters.AddWithValue("@cal", cal);
    }

    /// <summary>Hard-delete a custom channel by id (call <see cref="Backup"/> first).</summary>
    public void DeleteCustomChannel(long channelId)
    {
        using var c = Open();
        using var cmd = new SQLiteCommand("DELETE FROM user_customchanneldef WHERE ChannelId=@id", c);
        cmd.Parameters.AddWithValue("@id", channelId);
        cmd.ExecuteNonQuery();
    }
}
