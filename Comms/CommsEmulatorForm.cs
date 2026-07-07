using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DynoDynamicsCanFeeder;

/// <summary>
/// GUI for the comms-hub emulator: choose which devices report present, edit the data points
/// (weather etc.) to publish, and Start/Stop. Clearly experimental — the Param/Scale defaults are
/// guesses to be tuned against a live rig.
/// </summary>
public sealed class CommsEmulatorForm : Form
{
    // eDeviceId names worth emulating (mirrors DynoDotNet's enum).
    private static readonly string[] DeviceNames =
        { "COMMS_BOX", "WEATHER_STATION", "PIT_MASTER", "PIT_SLAVE", "EDM", "HANDHELD", "OBD2", "BED_CTRL" };

    private readonly TextBox _server = new() { Width = 150 };
    private readonly NumericUpDown _interval = new() { Minimum = 50, Maximum = 10000, Value = 500, Increment = 50, Width = 80 };
    private readonly CheckedListBox _devices = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly BindingList<CommsDataPoint> _points = new();
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false };
    private readonly Button _add = new() { Text = "Add", Width = 70 };
    private readonly Button _remove = new() { Text = "Remove", Width = 70 };
    private readonly Button _start = new() { Text = "Start", Width = 90 };
    private readonly Button _stop = new() { Text = "Stop", Width = 90, Enabled = false };
    private readonly Button _load = new() { Text = "Load", Width = 70 };
    private readonly Button _save = new() { Text = "Save", Width = 70 };
    private readonly Button _channels = new() { Text = "Custom Channels...", Width = 130 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Color.Lime };
    private readonly ToolTip _tip = new() { AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 100 };

    private CommsEmulator? _emu;

    public CommsEmulatorForm(CommsEmulatorConfig? cfg = null)
    {
        Text = "Comms Hub Emulator (experimental)";
        Width = 780;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        BuildUi();
        Apply(cfg ?? CommsEmulatorConfig.Default());
        FormClosed += (_, _) => { try { _emu?.Stop(); } catch { } };
    }

    private void BuildUi()
    {
        // Root grid: [top controls] / [devices | data points] / [buttons] / [log]
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(6) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // row 0: server/interval
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // row 1: devices | grid
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // row 2: buttons
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));  // row 3: log

        var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        top.Controls.Add(new Label { Text = "Dyno server:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(_server);
        top.Controls.Add(new Label { Text = "Interval (ms):", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        top.Controls.Add(_interval);
        root.Controls.Add(top, 0, 0);
        root.SetColumnSpan(top, 2);
        _tip.SetToolTip(_server, "DynoServer address. Blank = LAN broadcast discovery; 'loopback' = 127.0.0.1.");
        _tip.SetToolTip(_interval, "How often to re-announce presence and republish data points.");

        // Devices (left column)
        foreach (var d in DeviceNames) _devices.Items.Add(d);
        var devPanel = new Panel { Dock = DockStyle.Fill };
        devPanel.Controls.Add(_devices);
        devPanel.Controls.Add(new Label { Text = "Report present:", Dock = DockStyle.Top, Height = 20 });
        root.Controls.Add(devPanel, 0, 1);
        _tip.SetToolTip(_devices, "Each ticked device is announced to DigitalDyno as present via a DEVICE_STATUS packet.");

        // Data points (right column)
        _grid.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = nameof(CommsDataPoint.Name), Width = 95 },
            new DataGridViewComboBoxColumn { HeaderText = "Device", DataPropertyName = nameof(CommsDataPoint.Device), DataSource = DeviceNames, Width = 120, FlatStyle = FlatStyle.Flat },
            new DataGridViewTextBoxColumn { HeaderText = "Cmd", DataPropertyName = nameof(CommsDataPoint.Cmd), Width = 45, ToolTipText = "DynoCAN command byte (15 = DATA_VALUE)." },
            new DataGridViewTextBoxColumn { HeaderText = "Param", DataPropertyName = nameof(CommsDataPoint.Param), Width = 50, ToolTipText = "Field id within the device (guess; tune against a capture)." },
            new DataGridViewTextBoxColumn { HeaderText = "Flags", DataPropertyName = nameof(CommsDataPoint.Flags), Width = 45, ToolTipText = "data[1] sub/flags byte. EDM sensor frames use 1." },
            new DataGridViewTextBoxColumn { HeaderText = "Value", DataPropertyName = nameof(CommsDataPoint.Value), Width = 70, ToolTipText = "Physical value to publish (e.g. 25 for 25 C)." },
            new DataGridViewTextBoxColumn { HeaderText = "Scale", DataPropertyName = nameof(CommsDataPoint.Scale), Width = 60, ToolTipText = "raw = (Value - Offset) / Scale." },
            new DataGridViewTextBoxColumn { HeaderText = "Offset", DataPropertyName = nameof(CommsDataPoint.Offset), Width = 55 });
        _grid.DataSource = _points;
        var gridButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32 };
        gridButtons.Controls.Add(_add);
        gridButtons.Controls.Add(_remove);
        var ptPanel = new Panel { Dock = DockStyle.Fill };
        ptPanel.Controls.Add(_grid);
        ptPanel.Controls.Add(new Label { Text = "Data points (published as DATA_VALUE):", Dock = DockStyle.Top, Height = 20 });
        ptPanel.Controls.Add(gridButtons);
        root.Controls.Add(ptPanel, 1, 1);

        // Buttons (row 2, spans both columns)
        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        bottom.Controls.AddRange(new Control[] { _start, _stop, _load, _save, _channels });
        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);

        // Log (row 3, spans both columns)
        var logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_log);
        logPanel.Controls.Add(new Label { Text = "Log:", Dock = DockStyle.Top, Height = 18 });
        root.Controls.Add(logPanel, 0, 3);
        root.SetColumnSpan(logPanel, 2);

        Controls.Add(root);

        _add.Click += (_, _) => _points.Add(new CommsDataPoint { Name = "New", Device = "WEATHER_STATION", Cmd = 15, Scale = 0.1 });
        _remove.Click += (_, _) => { if (_grid.CurrentRow?.DataBoundItem is CommsDataPoint p) _points.Remove(p); };
        _start.Click += (_, _) => StartEmu();
        _stop.Click += (_, _) => StopEmu();
        _load.Click += (_, _) => LoadCfg();
        _save.Click += (_, _) => SaveCfg();
        _channels.Click += (_, _) => { using var f = new CustomChannelForm(); f.ShowDialog(this); };
    }

    private void Apply(CommsEmulatorConfig cfg)
    {
        _server.Text = cfg.ServerAddress;
        _interval.Value = Math.Min(_interval.Maximum, Math.Max(_interval.Minimum, cfg.IntervalMs));
        for (int i = 0; i < _devices.Items.Count; i++)
            _devices.SetItemChecked(i, cfg.PresentDevices.Contains((string)_devices.Items[i]!, StringComparer.OrdinalIgnoreCase));
        _points.Clear();
        foreach (var p in cfg.DataPoints) _points.Add(p);
    }

    private CommsEmulatorConfig Build()
    {
        var cfg = new CommsEmulatorConfig
        {
            ServerAddress = _server.Text.Trim(),
            IntervalMs = (int)_interval.Value,
            PresentDevices = _devices.CheckedItems.Cast<string>().ToList(),
            DataPoints = _points.ToList()
        };
        return cfg;
    }

    private void StartEmu()
    {
        try
        {
            _emu = new CommsEmulator(Build());
            _emu.Status += Log;
            _emu.Start();
            _start.Enabled = false;
            _stop.Enabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopEmu()
    {
        _emu?.Stop();
        _emu = null;
        _start.Enabled = true;
        _stop.Enabled = false;
    }

    private void LoadCfg()
    {
        using var dlg = new OpenFileDialog { Filter = "Comms config (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            try { Apply(CommsEmulatorConfig.Load(dlg.FileName)); Log("[cfg] loaded " + dlg.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void SaveCfg()
    {
        using var dlg = new SaveFileDialog { Filter = "Comms config (*.json)|*.json", FileName = "comms.config.json" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            try { Build().Save(dlg.FileName); Log("[cfg] saved " + dlg.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void Log(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(line))); } catch { } return; }
        _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
    }
}
