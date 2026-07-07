using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DynoDynamicsCanFeeder;

/// <summary>
/// In-app editor for DigitalDyno's ECU database (C:\DynoData\dyno-2.db for 2WD, dyno-4.db for 4WD):
/// create/edit the "CAN DBC Feeder"
/// ECU and its channel definitions, and import channels straight from the feeder's routes so
/// the CAN routing and the dyno-side presentation always match. Replaces hand-running feeder-ecu.sql.
/// </summary>
public sealed class EcuDbForm : Form
{
    private readonly IReadOnlyList<SignalRoute> _routes;
    private readonly ToolTip _tt = new() { AutoPopDelay = 30000, InitialDelay = 350, ShowAlways = true };

    private readonly TextBox _dbPath = new() { Width = 460, ReadOnly = true };
    private readonly Button _browse = new() { Text = "Browse...", Width = 80 };
    private readonly Button _auto = new() { Text = "Auto-locate", Width = 90 };

    private readonly ListBox _ecuList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _name = new() { Width = 240 };
    private readonly TextBox _desc = new() { Width = 240 };
    private readonly NumericUpDown _protocol = new() { Minimum = 0, Maximum = 9999, Width = 80 };
    private readonly NumericUpDown _tacho = new() { Minimum = 0, Maximum = 9999, Width = 80 };

    private readonly BindingList<ChannelDef> _channels = new();
    private readonly DataGridView _chGrid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true };
    private DataGridViewComboBoxColumn _unitCol = null!;

    private readonly Button _newEcu = new() { Text = "New ECU", Width = 90 };
    private readonly Button _deleteEcu = new() { Text = "Delete ECU", Width = 90 };
    private readonly Button _import = new() { Text = "Import from feeder routes", Width = 170, BackColor = Color.FromArgb(210, 230, 250) };
    private readonly Button _save = new() { Text = "Save to database", Width = 130, BackColor = Color.FromArgb(210, 240, 210) };
    private readonly Button _close = new() { Text = "Close", Width = 80 };

    private EcuDatabase? _db;
    private List<UnitInfo> _units = new();
    private EcuInfo _current = new() { ProtocolId = 20, Name = "CAN DBC Feeder" };

    public EcuDbForm(IReadOnlyList<SignalRoute> routes, string? dbPath)
    {
        _routes = routes;
        Text = "DigitalDyno ECU Database";
        Width = 900; Height = 620; MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterParent;

        BuildUi();
        WireEvents();

        string? path = dbPath ?? EcuDatabase.AutoLocate();
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) LoadDb(path!);
    }

    /// <summary>The DB path in use, so the caller can remember it between opens.</summary>
    public string? SelectedDbPath => _db?.Path;

    private void BuildUi()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.Add(new Label { Text = "ECU database (dyno-2.db):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_dbPath); top.Controls.Add(_browse); top.Controls.Add(_auto);

        // Left: ECU list + New/Delete
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        leftButtons.Controls.Add(_newEcu); leftButtons.Controls.Add(_deleteEcu);
        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 240, Padding = new Padding(8, 0, 4, 0) };
        leftPanel.Controls.Add(_ecuList); leftPanel.Controls.Add(leftButtons);
        leftPanel.Controls.Add(new Label { Text = "ECUs in database", Dock = DockStyle.Top, Height = 18 });

        // Right: ECU fields + channel grid
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true, Padding = new Padding(4) };
        fields.Controls.Add(new Label { Text = "Name", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        fields.Controls.Add(_name, 1, 0);
        fields.Controls.Add(new Label { Text = "Protocol ID", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 0);
        fields.Controls.Add(_protocol, 3, 0);
        fields.Controls.Add(new Label { Text = "Description", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        fields.Controls.Add(_desc, 1, 1);
        fields.Controls.Add(new Label { Text = "Tacho channel", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 1);
        fields.Controls.Add(_tacho, 3, 1);

        _chGrid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Ch#", DataPropertyName = nameof(ChannelDef.ChannelNo), Width = 45 },
            new DataGridViewTextBoxColumn { HeaderText = "Channel Name", DataPropertyName = nameof(ChannelDef.ChannelName), Width = 160 },
            new DataGridViewTextBoxColumn { HeaderText = "Short", DataPropertyName = nameof(ChannelDef.ShortName), Width = 90 },
            (_unitCol = new DataGridViewComboBoxColumn { HeaderText = "Unit", DataPropertyName = nameof(ChannelDef.UnitId), DisplayMember = "Label", ValueMember = "UnitId", Width = 150, FlatStyle = FlatStyle.Flat }),
            new DataGridViewTextBoxColumn { HeaderText = "Scale", DataPropertyName = nameof(ChannelDef.Scale), Width = 70 },
            new DataGridViewTextBoxColumn { HeaderText = "Offset", DataPropertyName = nameof(ChannelDef.Offset), Width = 60 },
            new DataGridViewTextBoxColumn { HeaderText = "DataType", DataPropertyName = nameof(ChannelDef.DataType), Width = 65 },
        });
        _chGrid.DataSource = _channels;
        _chGrid.DataError += (_, e) => e.ThrowException = false;

        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 8, 0) };
        rightPanel.Controls.Add(_chGrid);
        rightPanel.Controls.Add(new Label { Text = "Channel definitions (what the dyno shows for each channel number)", Dock = DockStyle.Top, Height = 18 });
        rightPanel.Controls.Add(fields);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        actions.Controls.AddRange(new Control[] { _import, _save, new Label { Width = 20 }, _close });

        Controls.Add(rightPanel);
        Controls.Add(leftPanel);
        Controls.Add(actions);
        Controls.Add(top);

        _tt.SetToolTip(_protocol, "Unique ID that groups this ECU's channel definitions. Select this same ECU in DigitalDyno.\nNew ECUs are suggested a free value >= 20 (vendor ECUs use 1..14).");
        _tt.SetToolTip(_tacho, "The channel number that carries engine RPM (0 = none). Matches a Tacho-ticked route.");
        _tt.SetToolTip(_import, "Fill the channel table from the feeder's current routes (channel #, scale, offset, and a guessed unit).");
        _tt.SetToolTip(_save, "Back up the DB, then insert/update this ECU and replace its channel rows. Close DigitalDyno first.");
        _tt.SetToolTip(_deleteEcu, "Delete the selected ECU and its channel definitions (with confirmation).");
    }

    private void WireEvents()
    {
        _browse.Click += (_, _) => BrowseDb();
        _auto.Click += (_, _) => { var p = EcuDatabase.AutoLocate(); if (p != null) LoadDb(p); else Warn(@"Could not find a live dyno-*.db under C:\DynoData (or the Documents template). Use Browse to pick it."); };
        _ecuList.SelectedIndexChanged += (_, _) => LoadSelectedEcu();
        _newEcu.Click += (_, _) => NewEcu();
        _deleteEcu.Click += (_, _) => DeleteEcu();
        _import.Click += (_, _) => ImportFromRoutes();
        _save.Click += (_, _) => SaveToDb();
        _close.Click += (_, _) => Close();
    }

    private void BrowseDb()
    {
        using var dlg = new OpenFileDialog { Filter = "Dyno DB (dyno-*.db)|dyno-*.db|SQLite (*.db)|*.db|All files (*.*)|*.*", FileName = "dyno-2.db" };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadDb(dlg.FileName);
    }

    private void LoadDb(string path)
    {
        try
        {
            _db = new EcuDatabase(path);
            _units = _db.ListUnits();
            _unitCol.DataSource = _units;
            _dbPath.Text = path;
            ReloadEcus();
            NewEcu();
        }
        catch (Exception ex) { Warn("Open failed: " + ex.Message); }
    }

    private void ReloadEcus()
    {
        if (_db is null) return;
        int keep = _ecuList.SelectedIndex;
        _ecuList.DataSource = null;
        _ecuList.DataSource = _db.ListEcus();
        if (keep >= 0 && keep < _ecuList.Items.Count) _ecuList.SelectedIndex = keep;
        else _ecuList.ClearSelected();
    }

    private void LoadSelectedEcu()
    {
        if (_db is null || _ecuList.SelectedItem is not EcuInfo ecu) return;
        _current = ecu;
        _name.Text = ecu.Name;
        _desc.Text = ecu.Description;
        _protocol.Value = ecu.ProtocolId;
        _tacho.Value = ecu.TachoChannel;
        _channels.Clear();
        foreach (var ch in _db.ListChannels(ecu.ProtocolId)) _channels.Add(ch);
    }

    private void NewEcu()
    {
        _ecuList.ClearSelected();
        _current = new EcuInfo { ProtocolId = _db?.SuggestProtocolId() ?? 20, Name = "CAN DBC Feeder" };
        _name.Text = _current.Name;
        _desc.Text = "Live CAN via Dyno Dynamics CAN Feeder";
        _protocol.Value = _current.ProtocolId;
        _tacho.Value = 0;
        _channels.Clear();
    }

    private void ImportFromRoutes()
    {
        if (_routes.Count == 0) { Warn("No routes defined in the feeder window to import."); return; }
        _channels.Clear();
        foreach (var r in _routes.OrderBy(x => x.ChannelNo))
        {
            var (uid, uname) = GuessUnit(r);
            _channels.Add(new ChannelDef
            {
                ChannelNo = r.ChannelNo,
                ChannelName = r.Signal,
                ShortName = r.Signal,
                UnitId = uid,
                UnitName = uname,
                Scale = r.Scale,
                Offset = r.Offset,
                DataType = 2,
            });
            if (r.IsTacho) _tacho.Value = r.ChannelNo;
        }
    }

    // Guess a sensible sys_unit for a route (161 rpm, 280 Lambda), else No Unit (0).
    private (int id, string name) GuessUnit(SignalRoute r)
    {
        string s = r.Signal.ToLowerInvariant();
        if (r.IsTacho || s.Contains("rpm") || s.Contains("engspd") || s.Contains("engine_speed")) return (161, "rpm");
        if (s.Contains("lambda") || Math.Abs(r.Scale - 0.001) < 1e-9) return (280, "Lambda");
        return (0, "");
    }

    private void SaveToDb()
    {
        if (_db is null) { Warn("Open a dyno-2.db (or dyno-4.db) first."); return; }
        _chGrid.EndEdit();
        string name = _name.Text.Trim();
        if (name.Length == 0) { Warn("Give the ECU a name."); return; }
        var channels = _channels.Where(c => !string.IsNullOrWhiteSpace(c.ChannelName)).ToList();
        if (channels.Count == 0) { Warn("Add at least one channel (or Import from feeder routes)."); return; }

        // fill UnitName from the chosen UnitId
        foreach (var ch in channels)
            ch.UnitName = _units.FirstOrDefault(u => u.UnitId == ch.UnitId)?.Name ?? ch.UnitName;

        var ecu = new EcuInfo
        {
            Id = _current.Id,
            Name = name,
            Description = _desc.Text.Trim(),
            TachoChannel = (int)_tacho.Value,
            ProtocolId = (int)_protocol.Value,
            ActiveChans = _current.ActiveChans,
        };

        try
        {
            string bak = _db.Backup();
            _db.SaveEcu(ecu, channels);
            _current = ecu;
            ReloadEcus();
            MessageBox.Show(this,
                $"Saved ECU '{ecu.Name}' (Protocol {ecu.ProtocolId}) with {channels.Count} channel(s).\n\n" +
                $"Backup: {Path.GetFileName(bak)}\n\n" +
                "Restart DigitalDyno (if open) and select this ECU to use it.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Warn("Save failed: " + ex.Message); }
    }

    private void DeleteEcu()
    {
        if (_db is null || _ecuList.SelectedItem is not EcuInfo ecu) { Warn("Select an ECU to delete."); return; }
        if (MessageBox.Show(this, $"Delete ECU '{ecu.Name}' (Protocol {ecu.ProtocolId}) and its channels?",
                "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { _db.Backup(); _db.DeleteEcu(ecu); ReloadEcus(); NewEcu(); }
        catch (Exception ex) { Warn("Delete failed: " + ex.Message); }
    }

    private void Warn(string msg) =>
        MessageBox.Show(this, msg, "ECU Database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
