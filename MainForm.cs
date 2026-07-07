using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using IxxatCanTool.Can;

namespace DynoDynamicsCanFeeder;

/// <summary>
/// WinForms front-end exposing every feeder option: adapter, device / WiFi endpoint, bit rate,
/// DBC, and the signal->channel routes, plus Load/Save config, Start/Stop, live values and a log.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ComboBox _adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 260 };
    private readonly Button _refresh = new() { Text = "Refresh", Width = 75 };
    private readonly ComboBox _bitrate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly TextBox _server = new() { Width = 160 };
    private readonly TextBox _dbc = new() { Width = 380, ReadOnly = true };
    private readonly Button _browse = new() { Text = "Browse...", Width = 80 };

    private readonly BindingList<SignalRoute> _routes = new();
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false };
    private readonly ListBox _signals = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    // DBC signal metadata for the currently-loaded DBC, keyed by signal name (for route pre-fill).
    private readonly Dictionary<string, FeederConfig.DbcSignalInfo> _dbcSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _addRoute = new() { Text = "Add", Width = 70 };
    private readonly Button _removeRoute = new() { Text = "Remove", Width = 70 };

    private readonly Button _database = new() { Text = "Database...", Width = 95 };
    private readonly Button _comms = new() { Text = "Comms Emu...", Width = 95 };
    private readonly Button _load = new() { Text = "Load Config", Width = 95 };
    private readonly Button _save = new() { Text = "Save Config", Width = 95 };
    private string? _lastDbPath;
    private readonly Button _start = new() { Text = "Start", Width = 90, BackColor = Color.FromArgb(210, 240, 210) };
    private readonly Button _stop = new() { Text = "Stop", Width = 90, Enabled = false };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.Black, ForeColor = Color.LightGreen, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private readonly DataGridView _live = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false };

    private readonly ToolTip _toolTip = new() { AutoPopDelay = 30000, InitialDelay = 350, ReshowDelay = 150, ShowAlways = true };
    private readonly Dictionary<string, double> _liveValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 250 };
    private Feeder? _feeder;

    public MainForm(string? configToLoad)
    {
        Text = "Dyno Dynamics CAN Feeder";
        Width = 940; Height = 680; MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        WireEvents();

        _adapter.Items.AddRange(new object[]
        {
            CanAdapterKind.IxxatVci, CanAdapterKind.Obdx, CanAdapterKind.J2534,
            CanAdapterKind.Gvret, CanAdapterKind.Peak, CanAdapterKind.VirtualTcp
        });
        _adapter.SelectedIndex = 0;
        foreach (var br in (CanBitRate[])Enum.GetValues(typeof(CanBitRate))) _bitrate.Items.Add(br);
        _bitrate.SelectedItem = CanBitRate.Br500kBit;
        RefreshDevices();

        if (!string.IsNullOrEmpty(configToLoad) && File.Exists(configToLoad))
            TryLoadConfig(configToLoad!);
    }

    private void BuildUi()
    {
        // --- settings panel (top) ---
        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 6, AutoSize = true, Padding = new Padding(8), Height = 120 };
        top.Controls.Add(new Label { Text = "Adapter", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        top.Controls.Add(_adapter, 1, 0);
        top.Controls.Add(new Label { Text = "Device / endpoint", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 0);
        var devPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        devPanel.Controls.Add(_device); devPanel.Controls.Add(_refresh);
        top.Controls.Add(devPanel, 3, 0);
        top.SetColumnSpan(devPanel, 3);

        top.Controls.Add(new Label { Text = "Bit rate", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        top.Controls.Add(_bitrate, 1, 1);
        top.Controls.Add(new Label { Text = "Dyno server", Anchor = AnchorStyles.Left, AutoSize = true }, 2, 1);
        top.Controls.Add(_server, 3, 1);

        top.Controls.Add(new Label { Text = "DBC file", Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        top.Controls.Add(_dbc, 1, 2);
        top.SetColumnSpan(_dbc, 3);
        top.Controls.Add(_browse, 4, 2);

        // --- routes + signals (middle) ---
        _grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "DBC Signal", DataPropertyName = nameof(SignalRoute.Signal), Width = 170,
                ToolTipText = "Exact signal name from the DBC to read (e.g. Engine_RPM, Lambda, Wideband_Overall)." },
            new DataGridViewTextBoxColumn { HeaderText = "Channel", DataPropertyName = nameof(SignalRoute.ChannelNo), Width = 65,
                ToolTipText = "The DigitalDyno ECU channel number this value is sent to. Must match a sys_ecuchanneldef.ChannelNo row (see feeder-ecu.sql)." },
            new DataGridViewTextBoxColumn { HeaderText = "Scale", DataPropertyName = nameof(SignalRoute.Scale), Width = 70,
                ToolTipText = "The Scale from that DB channel row. The feeder sends raw = (value - Offset) / Scale so the dyno reproduces the physical value. Lambda channels use 0.001; RPM uses 1." },
            new DataGridViewTextBoxColumn { HeaderText = "Offset", DataPropertyName = nameof(SignalRoute.Offset), Width = 65,
                ToolTipText = "The Offset from that DB channel row (usually 0)." },
            new DataGridViewCheckBoxColumn { HeaderText = "Tacho", DataPropertyName = nameof(SignalRoute.IsTacho), Width = 50,
                ToolTipText = "Tick for the RPM/tacho channel (the one set as sys_ecu.TachoChannel). Informational only." },
        });
        _grid.DataSource = _routes;
        _grid.ShowCellToolTips = true;
        _grid.DataError += (_, e) => e.ThrowException = false; // ignore bad inline edits

        var routeButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        routeButtons.Controls.Add(_addRoute); routeButtons.Controls.Add(_removeRoute);
        var routesPanel = new Panel { Dock = DockStyle.Fill };
        routesPanel.Controls.Add(_grid); routesPanel.Controls.Add(routeButtons);

        var signalsPanel = new Panel { Dock = DockStyle.Fill };
        signalsPanel.Controls.Add(_signals);
        signalsPanel.Controls.Add(new Label { Text = "DBC signals (double-click to add)", Dock = DockStyle.Top, Height = 18 });

        var midSplit = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 560 };
        midSplit.Panel1.Controls.Add(routesPanel);
        midSplit.Panel1.Controls.Add(new Label { Text = "Signal -> DigitalDyno channel routes", Dock = DockStyle.Top, Height = 18 });
        midSplit.Panel2.Controls.Add(signalsPanel);

        // --- live values + log (bottom) ---
        _live.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { HeaderText = "Signal", Name = "sig", Width = 160 },
            new DataGridViewTextBoxColumn { HeaderText = "Value", Name = "val", Width = 90 },
        });
        var bottomSplit = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260 };
        bottomSplit.Panel1.Controls.Add(_live);
        bottomSplit.Panel1.Controls.Add(new Label { Text = "Live decoded values", Dock = DockStyle.Top, Height = 18 });
        bottomSplit.Panel2.Controls.Add(_log);
        bottomSplit.Panel2.Controls.Add(new Label { Text = "Status log", Dock = DockStyle.Top, Height = 18 });

        var vSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 210 };
        vSplit.Panel1.Controls.Add(midSplit);
        vSplit.Panel2.Controls.Add(bottomSplit);

        // --- action bar ---
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
        actions.Controls.AddRange(new Control[] { _start, _stop, new Label { Width = 20 }, _database, _comms, _load, _save });

        Controls.Add(vSplit);
        Controls.Add(actions);
        Controls.Add(top);

        SetupTooltips();
    }

    /// <summary>Hover help for every field/button so the tool is self-explanatory.</summary>
    private void SetupTooltips()
    {
        _toolTip.SetToolTip(_adapter,
            "CAN interface hardware to read from:\n" +
            "  IxxatVci - IXXAT USB-to-CAN (VCI4 driver)\n" +
            "  Obdx - OBDX Pro (USB, WiFi or BLE)\n" +
            "  J2534 - any installed SAE J2534 PassThru cable (64-bit driver)\n" +
            "  Gvret - SavvyCAN-compatible GVRET logger (ESP32RET etc.) over serial or TCP\n" +
            "  Peak - PEAK PCAN-USB (PCANBasic driver)\n" +
            "  VirtualTcp - no cable: read a RawCanWire TCP feed from UltimateCANScanner /\n" +
            "               CAN-Replay (default 127.0.0.1:51729). Great for bench testing.");
        _toolTip.SetToolTip(_device,
            "The specific adapter / endpoint. Click Refresh to list connected devices.\n" +
            "OBDX/GVRET over WiFi: type the endpoint directly, e.g. tcp:192.168.4.1:23\n" +
            "(SoftAP default) or tcp:<lan-ip>:<port>. GVRET serial: pick serial:COMx\n" +
            "(add :baud to override, e.g. serial:COM5:1000000). J2534: pick the installed driver.");
        _toolTip.SetToolTip(_refresh, "Re-scan for connected devices (IXXAT / OBDX / J2534 drivers / serial ports).");
        _toolTip.SetToolTip(_bitrate,
            "CAN bus bit rate. Must match the device's bus:\n" +
            "  Haltech dash 1 Mbit  |  AEM X-Series & GM 500 kbit  |  ECOtrons ALM 250 kbit (default).");
        _toolTip.SetToolTip(_server,
            "DigitalDyno server (DynoServer) IP address.\n" +
            "Leave blank to auto-discover it on the LAN (recommended).");
        _toolTip.SetToolTip(_dbc,
            "The DBC file describing the CAN messages/signals on this bus.\n" +
            "Use the authored/verified DBCs in D:\\Projects\\Dyno (e.g. AEM_X-Series.dbc,\n" +
            "ECOtrons_ALM.dbc, Haltech_WB2.dbc, ECM_HS.dbc).");
        _toolTip.SetToolTip(_browse, "Choose the .dbc file for this source.");

        _toolTip.SetToolTip(_signals,
            "Signals found in the loaded DBC. Double-click one to add it as a route below.");
        _toolTip.SetToolTip(_addRoute, "Add a blank route row to map a DBC signal to a dyno channel.");
        _toolTip.SetToolTip(_removeRoute, "Remove the selected route row.");
        _toolTip.SetToolTip(_grid,
            "Each row forwards one decoded DBC signal to a DigitalDyno ECU channel.\n" +
            "Hover a column header for what to put in it.");

        _toolTip.SetToolTip(_live, "Live physical values for routed signals, updated ~4x/sec while running.");
        _toolTip.SetToolTip(_log, "Status messages: device connect, server register, and any errors.");

        _toolTip.SetToolTip(_start,
            "Open the adapter (listen-only), connect to DynoServer, and start feeding the routed signals.");
        _toolTip.SetToolTip(_stop, "Stop feeding and close the adapter + server connection.");
        _toolTip.SetToolTip(_load, "Load all settings above from a feeder*.config.json file.");
        _toolTip.SetToolTip(_save, "Save the current settings to a feeder*.config.json file.");
        _toolTip.SetToolTip(_database,
            "Edit DigitalDyno's ECU database (C:\\DynoData\\dyno-2.db for 2WD): create the 'CAN DBC Feeder' ECU and its\n" +
            "channel definitions, or Import them straight from the routes above. Replaces feeder-ecu.sql.");
        _toolTip.SetToolTip(_comms,
            "Comms-hub emulator (experimental): announce devices present (COMMS Interface, Weather\n" +
            "Station, Pit Box, EDM...) and feed weather data to DigitalDyno without the hardware.");
    }

    private void WireEvents()
    {
        _adapter.SelectedIndexChanged += (_, _) => RefreshDevices();
        _refresh.Click += (_, _) => RefreshDevices();
        _browse.Click += (_, _) => BrowseDbc();
        _addRoute.Click += (_, _) => _routes.Add(MakeRoute(""));
        _removeRoute.Click += (_, _) => { if (_grid.CurrentRow?.DataBoundItem is SignalRoute r) _routes.Remove(r); };
        _signals.DoubleClick += (_, _) =>
        {
            if (_signals.SelectedItem is string name)
                _routes.Add(MakeRoute(name));
        };
        _load.Click += (_, _) => LoadConfigDialog();
        _save.Click += (_, _) => SaveConfigDialog();
        _database.Click += (_, _) => OpenDatabaseEditor();
        _comms.Click += (_, _) => { using var f = new CommsEmulatorForm(); f.ShowDialog(this); };
        _start.Click += (_, _) => StartFeeder();
        _stop.Click += (_, _) => StopFeeder();
        _liveTimer.Tick += (_, _) => RefreshLive();
        FormClosing += (_, _) => StopFeeder();
    }

    private CanAdapterKind CurrentAdapter => (CanAdapterKind)_adapter.SelectedItem!;

    private void RefreshDevices()
    {
        _device.Items.Clear();
        try
        {
            foreach (var d in AdapterFactory.Enumerate(CurrentAdapter))
                _device.Items.Add(d.Key);
        }
        catch (Exception ex) { Log("[enumerate] " + ex.Message); }

        if (CurrentAdapter == CanAdapterKind.Obdx && !_device.Items.Contains("tcp:192.168.4.1:23"))
            _device.Items.Add("tcp:192.168.4.1:23");
        // GVRET boards are often on WiFi/TCP; offer the SavvyCAN default endpoint as a hint.
        if (CurrentAdapter == CanAdapterKind.Gvret && !_device.Items.Contains("tcp:192.168.4.1:23"))
            _device.Items.Add("tcp:192.168.4.1:23");
        if (_device.Items.Count > 0) _device.SelectedIndex = 0;
    }

    private void BrowseDbc()
    {
        using var dlg = new OpenFileDialog { Filter = "DBC files (*.dbc)|*.dbc|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) SetDbc(dlg.FileName);
    }

    private void SetDbc(string path)
    {
        _dbc.Text = path;
        _signals.Items.Clear();
        _dbcSignals.Clear();
        foreach (var sig in FeederConfig.ReadDbcSignals(path))
        {
            _signals.Items.Add(sig.Name);
            _dbcSignals[sig.Name] = sig;
        }
        Log($"[dbc] {Path.GetFileName(path)} — {_signals.Items.Count} signals");
    }

    /// <summary>
    /// Build a route for a DBC signal, pre-filled from the DBC: Scale/Offset default to the
    /// signal's Factor/Offset (so the dyno reproduces the physical value once a matching channel
    /// def is imported), and the channel number auto-increments so routes don't collide. RPM-like
    /// signals are flagged as the tacho. Falls back to sane defaults for a blank/unknown name.
    /// </summary>
    private SignalRoute MakeRoute(string name)
    {
        int channel = _routes.Count == 0 ? 1 : _routes.Max(r => r.ChannelNo) + 1;
        var route = new SignalRoute { Signal = name, ChannelNo = channel, Scale = 0.001, Offset = 0 };
        if (!string.IsNullOrEmpty(name) && _dbcSignals.TryGetValue(name, out var sig))
        {
            route.Scale = sig.Factor != 0 ? sig.Factor : 0.001;
            route.Offset = sig.Offset;
            bool looksLikeRpm = sig.Unit.Equals("rpm", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("rpm", StringComparison.OrdinalIgnoreCase) >= 0;
            if (looksLikeRpm) route.IsTacho = true;
        }
        return route;
    }

    private FeederConfig BuildConfig() => new()
    {
        Adapter = CurrentAdapter.ToString(),
        DeviceKey = _device.Text.Trim(),
        BitRate = ((CanBitRate)_bitrate.SelectedItem!).ToString(),
        DbcPath = _dbc.Text.Trim(),
        ServerAddress = _server.Text.Trim(),
        Routes = _routes.ToList()
    };

    private void ApplyConfig(FeederConfig c)
    {
        _adapter.SelectedItem = c.AdapterKind;
        RefreshDevices();
        _device.Text = c.DeviceKey;
        _bitrate.SelectedItem = c.BitRateValue;
        _server.Text = c.ServerAddress;
        if (!string.IsNullOrWhiteSpace(c.DbcPath) && File.Exists(ResolvePath(c.DbcPath)))
            SetDbc(ResolvePath(c.DbcPath));
        else _dbc.Text = c.DbcPath;
        _routes.Clear();
        foreach (var r in c.Routes) _routes.Add(r);
    }

    // Config DbcPath is often relative (e.g. ..\AEM_X-Series.dbc); resolve against the exe dir.
    private static string ResolvePath(string p) =>
        Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p));

    private void LoadConfigDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "Feeder config (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) TryLoadConfig(dlg.FileName);
    }

    private void TryLoadConfig(string path)
    {
        try { ApplyConfig(FeederConfig.Load(path)); Log($"[config] loaded {Path.GetFileName(path)}"); }
        catch (Exception ex) { Warn("Load failed: " + ex.Message); }
    }

    private void SaveConfigDialog()
    {
        using var dlg = new SaveFileDialog { Filter = "Feeder config (*.json)|*.json", FileName = "feeder.config.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { BuildConfig().Save(dlg.FileName); Log($"[config] saved {Path.GetFileName(dlg.FileName)}"); }
        catch (Exception ex) { Warn("Save failed: " + ex.Message); }
    }

    private void OpenDatabaseEditor()
    {
        using var dlg = new EcuDbForm(_routes.ToList(), _lastDbPath);
        dlg.ShowDialog(this);
        _lastDbPath = dlg.SelectedDbPath ?? _lastDbPath;
        Log("[db] editor closed");
    }

    private void StartFeeder()
    {
        var cfg = BuildConfig();
        if (string.IsNullOrWhiteSpace(cfg.DbcPath)) { Warn("Choose a DBC file first."); return; }
        cfg.DbcPath = ResolvePath(cfg.DbcPath);
        if (cfg.Routes.Count == 0) { Warn("Add at least one signal route."); return; }

        try
        {
            _feeder = new Feeder(cfg);
            _feeder.Status += OnStatus;
            _feeder.SignalDecoded += OnSignal;
            _feeder.Start();
            _liveValues.Clear();
            _liveTimer.Start();
            SetRunning(true);
            Log("[feeder] started");
        }
        catch (Exception ex)
        {
            Warn("Start failed: " + ex.Message);
            _feeder?.Dispose();
            _feeder = null;
        }
    }

    private void StopFeeder()
    {
        _liveTimer.Stop();
        if (_feeder is null) return;
        _feeder.Status -= OnStatus;
        _feeder.SignalDecoded -= OnSignal;
        try { _feeder.Dispose(); } catch { /* best effort */ }
        _feeder = null;
        SetRunning(false);
        Log("[feeder] stopped");
    }

    private void SetRunning(bool running)
    {
        _start.Enabled = !running;
        _stop.Enabled = running;
        foreach (var c in new Control[] { _adapter, _device, _refresh, _bitrate, _server, _browse, _load })
            c.Enabled = !running;
    }

    private void OnStatus(string line) => UiInvoke(() => Log(line));
    private void OnSignal(string name, double value) { lock (_liveValues) _liveValues[name] = value; }

    private void RefreshLive()
    {
        KeyValuePair<string, double>[] snapshot;
        lock (_liveValues) snapshot = _liveValues.OrderBy(k => k.Key).ToArray();
        _live.Rows.Clear();
        foreach (var kv in snapshot) _live.Rows.Add(kv.Key, kv.Value.ToString("0.####"));
    }

    private void Log(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss}  {line}\r\n";
        if (_log.TextLength > 20000) _log.Clear();
        _log.AppendText(stamped);
    }

    private void Warn(string msg) { Log("[warn] " + msg); MessageBox.Show(this, msg, "Dyno Dynamics CAN Feeder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

    private void UiInvoke(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }
}
