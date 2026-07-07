using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DynoDynamicsCanFeeder;

/// <summary>
/// Editor for DigitalDyno's custom sensor channels (user_customchanneldef) — e.g. the EDM air-temp
/// input + extra custom sensors. Each row is a device input (SourceId = the decimal of the wire
/// DATA_VALUE param) calibrated 2-point from volts to a real unit. Pairs with the comms emulator,
/// which sends the matching raw values. Backs up dyno-2.db and writes in one pass.
/// </summary>
public sealed class CustomChannelForm : Form
{
    private readonly ToolTip _tt = new() { AutoPopDelay = 30000, InitialDelay = 350, ShowAlways = true };
    private readonly TextBox _dbPath = new() { Width = 430, ReadOnly = true };
    private readonly Button _browse = new() { Text = "Browse...", Width = 80 };
    private readonly Button _auto = new() { Text = "Auto-locate", Width = 90 };

    private readonly BindingList<CustomChannel> _rows = new();
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false };
    private DataGridViewComboBoxColumn _unitCol = null!;

    private readonly Button _add = new() { Text = "Add", Width = 70 };
    private readonly Button _addEdm = new() { Text = "Add EDM Inputs 1-4", Width = 150, BackColor = Color.FromArgb(210, 230, 250) };
    private readonly Button _remove = new() { Text = "Remove", Width = 70 };
    private readonly Button _save = new() { Text = "Save to database", Width = 130, BackColor = Color.FromArgb(210, 240, 210) };
    private readonly Button _close = new() { Text = "Close", Width = 80 };

    private EcuDatabase? _db;
    private List<UnitInfo> _units = new();
    private readonly HashSet<long> _loadedIds = new();  // ids present on load, to detect deletions

    public CustomChannelForm(string? dbPath = null)
    {
        Text = "EDM / Custom Sensor Channels";
        Width = 860; Height = 520; MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterParent;
        BuildUi();
        WireEvents();
        string? path = dbPath ?? EcuDatabase.AutoLocate();
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) LoadDb(path!);
    }

    private void BuildUi()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        top.Controls.Add(new Label { Text = "Database (dyno-2.db):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_dbPath); top.Controls.Add(_browse); top.Controls.Add(_auto);

        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewCheckBoxColumn { HeaderText = "On", DataPropertyName = nameof(CustomChannel.IsActive), Width = 36, ToolTipText = "IsActive - show this channel in DigitalDyno." },
            new DataGridViewTextBoxColumn { HeaderText = "Channel Name", DataPropertyName = nameof(CustomChannel.ChannelName), Width = 150 },
            new DataGridViewTextBoxColumn { HeaderText = "Short", DataPropertyName = nameof(CustomChannel.ShortName), Width = 80 },
            (_unitCol = new DataGridViewComboBoxColumn { HeaderText = "Unit", DataPropertyName = nameof(CustomChannel.UnitId), DisplayMember = "Label", ValueMember = "UnitId", Width = 140, FlatStyle = FlatStyle.Flat }),
            new DataGridViewTextBoxColumn { HeaderText = "SourceId", DataPropertyName = nameof(CustomChannel.SourceId), Width = 65, ToolTipText = "Device input = decimal of the DATA_VALUE param the emulator sends (0x19 -> 25)." },
            new DataGridViewTextBoxColumn { HeaderText = "V₀", DataPropertyName = nameof(CustomChannel.CalIn0), Width = 55, ToolTipText = "Calibration point 0 - input volts." },
            new DataGridViewTextBoxColumn { HeaderText = "out₀", DataPropertyName = nameof(CustomChannel.CalOut0), Width = 55, ToolTipText = "Calibration point 0 - output in the chosen unit." },
            new DataGridViewTextBoxColumn { HeaderText = "V₁", DataPropertyName = nameof(CustomChannel.CalIn1), Width = 55, ToolTipText = "Calibration point 1 - input volts." },
            new DataGridViewTextBoxColumn { HeaderText = "out₁", DataPropertyName = nameof(CustomChannel.CalOut1), Width = 55, ToolTipText = "Calibration point 1 - output in the chosen unit." },
        });
        _grid.DataSource = _rows;
        _grid.DataError += (_, e) => e.ThrowException = false;

        var gridButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        gridButtons.Controls.AddRange(new Control[] { _add, _addEdm, _remove });
        var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        mid.Controls.Add(_grid);
        mid.Controls.Add(new Label { Text = "Custom sensor channels (calibrated volts → unit):", Dock = DockStyle.Top, Height = 18 });
        mid.Controls.Add(gridButtons);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        actions.Controls.AddRange(new Control[] { _save, new Label { Width = 20 }, _close });

        Controls.Add(mid);
        Controls.Add(actions);
        Controls.Add(top);

        _tt.SetToolTip(_addEdm, "Seed the EDM air-temp channel (raw 600 ≈ 18 C) plus 3 blank custom sensors.");
        _tt.SetToolTip(_save, "Back up dyno-2.db, then insert/update these channels (and delete removed ones). Close DigitalDyno first.");
    }

    private void WireEvents()
    {
        _browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "Dyno DB (dyno-*.db)|dyno-*.db|SQLite (*.db)|*.db|All files|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) LoadDb(dlg.FileName);
        };
        _auto.Click += (_, _) => { var p = EcuDatabase.AutoLocate(); if (p != null) LoadDb(p); else Warn(@"Could not find a live dyno-*.db under C:\DynoData."); };
        _add.Click += (_, _) => _rows.Add(new CustomChannel { ChannelName = "Custom", ShortName = "Custom", UnitId = 200, CalIn1 = 5, CalOut1 = 5 });
        _addEdm.Click += (_, _) => AddEdmDefaults();
        _remove.Click += (_, _) => { if (_grid.CurrentRow?.DataBoundItem is CustomChannel r) _rows.Remove(r); };
        _save.Click += (_, _) => SaveToDb();
        _close.Click += (_, _) => Close();
    }

    private void LoadDb(string path)
    {
        try
        {
            _db = new EcuDatabase(path);
            _units = _db.ListUnits();
            _unitCol.DataSource = _units;
            _dbPath.Text = path;
            _rows.Clear();
            _loadedIds.Clear();
            foreach (var cc in _db.ListCustomChannels()) { _rows.Add(cc); if (cc.ChannelId is { } id) _loadedIds.Add(id); }
        }
        catch (Exception ex) { Warn("Open failed: " + ex.Message); }
    }

    private void AddEdmDefaults()
    {
        // NB inlet air temp is a BUILT-IN channel (EDM param 0x24), not a custom channel - not seeded.
        // These are the EDM's 4 general-purpose inputs. SourceId = the live wire param confirmed from
        // the FTDI capture (0x20/0x28/0x3A/0x3B = 32/40/58/59) - the stock "EDM Input #1-4" rows use
        // mismatched SourceIds (18/175/19/25) which is why they read nothing. Rename/recalibrate to
        // the sensor you wire to each input.
        int[] param = { 32, 40, 58, 59 };
        for (int i = 0; i < 4; i++)
            _rows.Add(new CustomChannel { ChannelName = $"EDM Input {i + 1}", ShortName = $"EDM{i + 1}", UnitId = 200, SourceId = param[i], CalIn0 = 0, CalOut0 = 0, CalIn1 = 5, CalOut1 = 5 });
    }

    private void SaveToDb()
    {
        if (_db is null) { Warn("Open a dyno-2.db first."); return; }
        _grid.EndEdit();
        var rows = _rows.Where(r => !string.IsNullOrWhiteSpace(r.ChannelName)).ToList();
        if (rows.Count == 0) { Warn("Add at least one channel."); return; }

        try
        {
            string bak = _db.Backup();
            // delete rows that were loaded but removed from the grid
            var keep = new HashSet<long>(rows.Where(r => r.ChannelId is { }).Select(r => r.ChannelId!.Value));
            foreach (long id in _loadedIds.Where(id => !keep.Contains(id))) _db.DeleteCustomChannel(id);
            foreach (var r in rows) _db.SaveCustomChannel(r);

            _loadedIds.Clear();
            foreach (var r in rows) if (r.ChannelId is { } id) _loadedIds.Add(id);
            MessageBox.Show(this,
                $"Saved {rows.Count} custom channel(s).\n\nBackup: {Path.GetFileName(bak)}\n\n" +
                "Restart DigitalDyno to see them (add them to a screen / gauge).",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { Warn("Save failed: " + ex.Message); }
    }

    private void Warn(string msg) =>
        MessageBox.Show(this, msg, "Custom Channels", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
