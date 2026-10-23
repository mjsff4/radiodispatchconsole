using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    // Main operator position. Split across partial files:
    //   ConsoleWindow.cs       layout, theme, cards, display, keys, tick loop, logging, persistence
    //   ConsoleWindow.Tabs.cs  tab page builders (control, alerts, head, mixer, recorder, diagnostics)
    //   ConsoleWindow.Live.cs  hardware links, PTT, tones, SB9600 processing, actions/macros, recorder
    public sealed partial class ConsoleWindow : Form
    {
        internal enum OpMode { Simulation, LiveReceive, Live }
        public const string VersionText = "v1.0.2";

        private DispatchConfig _config;
        private readonly string _base, _path, _data;
        private UiTheme _t;
        private readonly Dictionary<string, RadioState> _states = new Dictionary<string, RadioState>();
        private readonly Dictionary<string, Form> _audioForms = new Dictionary<string, Form>();
        private readonly Dictionary<string, AudioPane> _audioPanes = new Dictionary<string, AudioPane>();
        private readonly TxController _tx = new TxController();
        private readonly List<PageJob> _queue = new List<PageJob>();
        private readonly List<string> _history = new List<string>();
        private readonly CallRecorder _recorder = new CallRecorder();
        private readonly Timer _tick = new Timer();
        private readonly ToolTip _tips = new ToolTip();
        private RadioProfile _selected;
        private OpMode _opMode = OpMode.Simulation;
        private bool _armed, _armConfirmed;
        private bool _refreshing, _queueRunning, _closing;
        private Keys _heldPttKey = Keys.None;
        private LocalPreview _preview;
        private DateTime _lastBeacon = DateTime.UtcNow;
        private int _pulse, _callsVersion = -1, _viewZone;
        private string _viewZoneRadio = "";

        // ---- controls (re-created by BuildUi) ----
        private Control _rootControl;
        private ListBox _events, _queueList, _calls;
        private FlowLayoutPanel _cards, _alertButtons, _softButtons, _quick, _channelKeys, _headButtons;
        private ComboBox _zone, _channel, _presets, _playDevice;
        private Label _clock, _modeBadge, _txStatus, _footer, _queueState, _mixerNote, _recStatus, _brand, _headMirror;
        private LcdPanel _lcd;
        private Dictionary<string, Label> _indicators = new Dictionary<string, Label>();
        private Dictionary<string, RadioCard> _cardControls = new Dictionary<string, RadioCard>();
        private Dictionary<string, TrackBar> _sliders = new Dictionary<string, TrackBar>();
        private Dictionary<string, Label> _sliderLabels = new Dictionary<string, Label>();
        private Button _ptt, _groupPtt, _allPtt, _arm;
        private TextBox _trace, _raw, _digits, _note;
        private LevelMeter _rxMeter, _txMeter, _micMeter;

        public ConsoleWindow() : this(null) { }
        internal ConsoleWindow(string configPath) : this(configPath, false) { }
        // testMode: start in Test mode whatever the settings say (the self-test drives the window with no hardware).
        internal ConsoleWindow(string configPath, bool testMode)
        {
            _base = AppDomain.CurrentDomain.BaseDirectory;
            // Settings live in the shared settings folder (AppPaths.DataDir), so a new version folder finds them; the
            // first start there imports the newest older version's settings automatically.
            // (An explicit configPath - the self-test - keeps everything, first-run files included, in that file's folder.)
            string imported = ""; bool shared = configPath == null;
            if (shared) { imported = AppPaths.FirstRunImport(AppPaths.DataDir); configPath = Path.Combine(AppPaths.DataDir, AppPaths.ConfigName); }
            _path = configPath; _data = Path.GetDirectoryName(_path);
            if (shared) try { string shipped = Path.Combine(_base, "audio.settings"), mine = Path.Combine(_data, "audio.settings"); if (!File.Exists(mine) && File.Exists(shipped)) File.Copy(shipped, mine); } catch (Exception) { }
            _config = LoadSettings(shared ? _base : _data);
            if (imported.Length > 0) _config.SeedNote = (imported.StartsWith("could not") ? "Settings: " : "Settings imported automatically: ") + imported + ". " + (_config.SeedNote ?? "");
            foreach (RadioProfile radio in _config.Radios) _states[radio.Id] = new RadioState();
            _selected = _config.Radios[0];
            _recorder.BaseFolder = _data; _recorder.Settings = _config.Recorder; _recorder.ChannelOf = ChannelLabel;
            Text = Brand.Product + " " + VersionText; Brand.Apply(this);
            KeyPreview = true; AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new Size(800, 560);
            RestoreWindow();
            KeyUp += OnKeyUpHandler;
            ResizeEnd += delegate { ScheduleLayoutCheck(); };
            SizeChanged += delegate { if (WindowState != _lastState) { _lastState = WindowState; ScheduleLayoutCheck(); } };
            Shown += delegate
            {
                _lastState = WindowState; ScheduleLayoutCheck();
                if (_rxStartPending) { _rxStartPending = false; if (_opMode != OpMode.Simulation && _config.Recorder.AutoStartRx) StartReceiveAudio(); }
            };
            // A held keyboard or mouse PTT ends when the console loses focus; a footswitch keeps working whatever has focus.
            Deactivate += delegate { if (_footHeld) return; if (_heldPttKey != Keys.None || IsHoldPtt()) { _heldPttKey = Keys.None; StopTx("Window lost focus"); } };
            FormClosing += delegate { _closing = true; DisposeTools(); StopAll("Console closing"); RememberWindow(); CloseLinks(); CloseAudio(); CloseFootswitch(); if (!_skipSaveOnClose) Save(); _tick.Stop(); };
            FormClosed += delegate { _tick.Dispose(); _tips.Dispose(); if (_t != null) _t.Dispose(); };
            _tick.Interval = 50; _tick.Tick += delegate { Tick(); };
            _opMode = testMode ? OpMode.Simulation : _config.StartIn == StartupMode.Test ? OpMode.Simulation : _config.StartIn == StartupMode.ReceiveOnly ? OpMode.LiveReceive : OpMode.Live;
            BuildUi();
            _tick.Start();
            Log(Brand.Product.ToUpperInvariant() + " " + VersionText + " started.");
            SetMode(_opMode);
            Log("Hold PTT or " + Hotkey.Display(_config.Hotkeys.Ptt) + " to talk. " + Hotkey.Display(_config.Hotkeys.StopAll) + " stops everything. Console > Keyboard shortcuts lists every key.");
            if (!String.IsNullOrEmpty(_config.SeedNote)) Log(_config.SeedNote);
            ReportHotkeyProblems();
        }

        // =====================================================================
        // Layout
        // =====================================================================
        private void BuildUi()
        {
            SuspendLayout();
            DisposeTools();
            if (_rootControl != null) { Controls.Remove(_rootControl); _rootControl.Dispose(); _rootControl = null; }
            UiTheme old = _t; _t = UiTheme.Create(_config.Ui);
            BackColor = _t.Back; ForeColor = _t.Text; Font = _t.Base;
            _indicators = new Dictionary<string, Label>(); _cardControls = new Dictionary<string, RadioCard>();
            _sliders = new Dictionary<string, TrackBar>(); _sliderLabels = new Dictionary<string, Label>();
            // Tab controls are optional (tabs can be hidden): clear references to the previous build.
            _events = null; _queueList = null; _calls = null; _alertButtons = null; _softButtons = null; _channelKeys = null; _headButtons = null;
            _presets = null; _playDevice = null; _queueState = null; _mixerNote = null; _recStatus = null; _headMirror = null;
            _trace = null; _raw = null; _digits = null; _note = null; _rxMeter = null; _txMeter = null; _micMeter = null; _cards = null; _quick = null;
            UiSettings ui = _config.Ui;
            _compact = WantCompact(); _layoutDue = 0;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(_t.S(6)), BackColor = _t.Back, Margin = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, _compact ? Math.Max(_t.S(42), _t.Big.Height + _t.S(14)) : Math.Max(_t.S(70), _t.Huge.Height + _t.Small.Height + _t.S(18))));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(_t.S(34), _t.Bold.Height + _t.S(14))));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, ui.ShowQuickKeys ? QuickBarHeight() : 0));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, _compact ? Math.Max(_t.S(54), _t.Big.Height * 2 + _t.S(12)) : Math.Max(_t.S(70), _t.ButtonH * 2 + _t.S(10))));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, ui.ShowJournal ? (_compact ? Math.Min(_t.S(ui.JournalHeight), _t.S(66)) : _t.S(ui.JournalHeight)) : 0));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(_compact ? 20 : 24)));
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildToolbar(), 0, 1);
            root.Controls.Add(BuildMain(), 0, 2);
            _quick = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = _t.Back, WrapContents = true, AutoScroll = true, Padding = new Padding(0), Margin = new Padding(0) };
            _quick.Visible = ui.ShowQuickKeys; root.Controls.Add(_quick, 0, 3);
            root.Controls.Add(BuildPttBar(), 0, 4);
            root.Controls.Add(BuildJournal(), 0, 5);
            _footer = _t.MakeLabel("", _t.Small, _t.Warn); _footer.Dock = DockStyle.Fill; root.Controls.Add(_footer, 0, 6);
            Controls.Add(root); _rootControl = root;
            _journalWant = root.RowStyles[5].Height;
            root.SizeChanged += delegate { FitCompactRows(); };
            ResumeLayout(true);
            if (old != null) old.Dispose();
            _quick.Resize += delegate { LayoutQuickKeys(); };
            RebuildQuickKeys(); RebuildCards(); RefreshSelected(); RefreshQueue(); _callsVersion = -1;
        }

        // ---- screen size: Full layout on large screens, Compact on small ones (1024x768, 1366x768 laptops) ----
        private bool _compact, _cardsInTab;
        private int _layoutDue;
        private FormWindowState _lastState = FormWindowState.Normal;
        private float _journalWant;
        // Height the main area needs in Compact: radio display, lights, channel row, the tab strip and one row of channel keys.
        internal int CompactMainNeed()
        {
            return _t.S(8) + _t.S(94) + _t.S(26) + _t.ButtonH + _t.S(12) + _t.S(36) + _t.S(8) + _t.ButtonH + _t.S(6) + ChannelKeyButton.PreferredHeight(_t) + _t.S(6);
        }
        // Compact on a short window: the journal gives up height (and hides below a readable size) so the channel keys
        // always keep at least one full row. It comes back when the window is taller.
        private void FitCompactRows()
        {
            var root = _rootControl as TableLayoutPanel;
            if (root == null || root.RowStyles.Count < 7 || !_config.Ui.ShowJournal) return;
            float want = _journalWant;
            if (_compact)
            {
                float fixedRows = 0; for (int i = 0; i < root.RowStyles.Count; i++) if (i != 2 && i != 5) fixedRows += root.RowStyles[i].Height;
                float room = root.ClientSize.Height - root.Padding.Vertical - fixedRows - CompactMainNeed();
                want = Math.Max(0, Math.Min(_journalWant, room));
                if (want < _t.S(36)) want = 0;
            }
            if (Math.Abs(root.RowStyles[5].Height - want) > 0.5f) root.RowStyles[5].Height = want;
        }
        // The space the console can use: its own client area once shown and not maximised, otherwise the screen it is on.
        private Size AvailableSize()
        {
            if (WindowState == FormWindowState.Normal && ClientSize.Width > 0) return ClientSize;
            Rectangle area = Screen.FromRectangle(WindowState == FormWindowState.Normal ? Bounds : RestoreBounds).WorkingArea;
            return new Size(area.Width - SystemInformation.FrameBorderSize.Width * 2, area.Height - SystemInformation.CaptionHeight - SystemInformation.FrameBorderSize.Height * 2);
        }
        internal bool WantCompact()
        {
            if (_config.Ui.Layout == LayoutMode.Compact) return true;
            if (_config.Ui.Layout == LayoutMode.Full) return false;
            Size s = AvailableSize(); double k = _t == null ? 1.0 : _t.Scale;
            return s.Width < 1400 * k || s.Height < 820 * k;
        }
        // Called on resize / maximise: rebuilds only when the layout would change, after the size settles, and never mid-TX.
        private void ScheduleLayoutCheck() { if (_t != null && _config.Ui.Layout == LayoutMode.Auto) _layoutDue = Environment.TickCount + 500; }
        private void ProcessLayoutCheck()
        {
            if (_layoutDue == 0 || Environment.TickCount - _layoutDue < 0 || WindowState == FormWindowState.Minimized) return;
            if (_tx.Active || _queueRunning || _seeks.Count > 0) { _layoutDue = Environment.TickCount + 1000; return; }
            _layoutDue = 0;
            if (WantCompact() == _compact) return;
            BuildUi(); Log("Window size changed: " + (_compact ? "Compact" : "Full") + " layout (Settings > Appearance > Layout can force one).");
        }
        // Shrinks a window or dialog to the screen it opens on (so nothing is off-screen on a 1024x768 laptop).
        internal static Size FitToScreen(Size wanted)
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            return new Size(Math.Min(wanted.Width, area.Width - 20), Math.Min(wanted.Height, area.Height - 20));
        }

        // Quick keys are two lines tall (label + the key that triggers it) when any key has a hotkey.
        private int QuickKeyHeight() { return _config.QuickKeys.Exists(q => Hotkey.Parse(q.Hotkey) != Keys.None) ? _t.ButtonH + _t.S(14) : _t.ButtonH; }
        // Compact: as many keys per row as fit (at least QuickKeyColumns), so the bar usually needs one row.
        private int QuickColumns(int width)
        {
            int cols = Math.Max(1, _config.Ui.QuickKeyColumns);
            if (!_compact) return cols;
            return Math.Max(cols, Math.Min(_config.QuickKeys.Count, (width - _t.S(64)) / _t.S(86)));
        }
        private int QuickBarHeight()
        {
            int cols = QuickColumns(AvailableSize().Width - _t.S(16)), rows = Math.Min(_compact ? 2 : 3, (_config.QuickKeys.Count + cols - 1) / cols);
            return Math.Max(1, rows) * (QuickKeyHeight() + _t.S(6)) + _t.S(4);
        }

        private Control BuildHeader()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = _t.Panel, Padding = new Padding(_t.S(6), _t.S(3), _t.S(10), _t.S(3)), Margin = new Padding(0) };
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _t.S(_compact ? 40 : 64))); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _t.S(_compact ? 230 : 300))); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _t.S(_compact ? 150 : 190)));
            var logo = new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0), Cursor = Cursors.Hand };
            logo.Click += delegate { ShowAbout(); }; _tips.SetToolTip(logo, Brand.Product + " - click for About");
            row.Controls.Add(logo, 0, 0);
            // Title over motto; rows sized from the actual fonts so neither line is clipped at any scale.
            var names = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent, Margin = new Padding(_t.S(8), 0, 0, 0) };
            names.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            names.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); names.RowStyles.Add(new RowStyle(SizeType.Absolute, _compact ? 0 : _t.Small.Height + _t.S(6)));
            _brand = _t.MakeLabel(_config.ConsoleName + "   DISPATCH", _compact ? _t.Big : _t.Huge, _t.Accent); _brand.Dock = DockStyle.Fill; _brand.TextAlign = ContentAlignment.BottomLeft; _brand.AutoEllipsis = true;
            Label motto = _t.MakeLabel(Brand.Motto, _t.Small, _t.Warn); motto.Dock = DockStyle.Fill; motto.TextAlign = ContentAlignment.TopLeft; motto.AutoEllipsis = true; motto.Visible = !_compact;
            if (_compact) _brand.TextAlign = ContentAlignment.MiddleLeft;
            names.Controls.Add(_brand, 0, 0); names.Controls.Add(motto, 0, 1);
            row.Controls.Add(names, 1, 0);
            _modeBadge = _t.MakeLabel("", _t.Big, _t.Back); _modeBadge.Dock = DockStyle.Fill; _modeBadge.TextAlign = ContentAlignment.MiddleCenter; _modeBadge.Margin = _compact ? new Padding(_t.S(4), _t.S(4), _t.S(4), _t.S(4)) : new Padding(_t.S(6), _t.S(12), _t.S(6), _t.S(12));
            row.Controls.Add(_modeBadge, 2, 0);
            _clock = _t.MakeLabel("", _t.Bold, _t.Accent); _clock.Dock = DockStyle.Fill; _clock.TextAlign = ContentAlignment.MiddleRight; row.Controls.Add(_clock, 3, 0);
            return row;
        }

        private Control BuildToolbar() { return BuildMenuBar(); }

        private Control BuildMain()
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = _t.Back, Margin = new Padding(0, _t.S(4), 0, _t.S(4)) };
            UiSettings ui = _config.Ui;
            _cards = new FlowLayoutPanel { AutoScroll = true, BackColor = _t.Back, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 0, _t.S(4), 0) };
            // Compact: one radio needs no cards (the display above already shows it); several radios get a RADIOS tab
            // (BuildOperations) so the cards never take space from the channel keys.
            _cardsInTab = ui.ShowCards && _compact && _config.Radios.Count > 1;
            if (_cardsInTab) { _cards.Dock = DockStyle.Fill; _cards.WrapContents = true; _cards.Resize += delegate { LayoutCards(); }; }
            Control center = BuildSelected(); center.Dock = DockStyle.Fill;
            host.Controls.Add(center);
            bool showCards = ui.ShowCards && !_compact;
            if (showCards)
            {
                var splitter = new Splitter { BackColor = _t.Border };
                if (ui.Placement == CardPlacement.Left && !_compact)
                {
                    _cards.Dock = DockStyle.Left; _cards.WrapContents = true; _cards.Width = _t.S(ui.CardsWidth);
                    splitter.Dock = DockStyle.Left; splitter.Width = _t.S(5); splitter.MinSize = _t.S(180); splitter.MinExtra = _t.S(480);
                }
                else
                {
                    _cards.Dock = DockStyle.Top; _cards.WrapContents = false; _cards.Height = CardHeight() + _t.S(26);
                    splitter.Dock = DockStyle.Top; splitter.Height = _t.S(5); splitter.MinSize = _t.S(60); splitter.MinExtra = _t.S(300);
                }
                splitter.SplitterMoved += delegate
                {
                    if (_config.Ui.Placement == CardPlacement.Left && !_compact) _config.Ui.CardsWidth = Math.Max(200, Math.Min(1000, (int)(_cards.Width / _t.Scale)));
                    LayoutCards();
                };
                host.Controls.Add(splitter); host.Controls.Add(_cards);
                _cards.Resize += delegate { LayoutCards(); };
            }
            else if (!_cardsInTab) { _cards.Dispose(); _cards = null; }
            return host;
        }

        private int CardHeight()
        {
            CardDensity d = _config.CompactCards || _compact ? CardDensity.Compact : _config.Ui.Density;
            return d == CardDensity.Compact ? _t.S(70) + _t.ButtonH : d == CardDensity.Large ? _t.S(132) + _t.ButtonH : _t.S(104) + _t.ButtonH;
        }

        private Control BuildSelected()
        {
            var panel = new TableLayoutPanel { RowCount = 4, ColumnCount = 1, BackColor = _t.Back, Margin = new Padding(0), Padding = new Padding(_t.S(4), 0, 0, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(_compact ? 94 : 116)));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(_compact ? 26 : 32)));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.ButtonH + _t.S(_compact ? 12 : 18)));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _lcd = new LcdPanel(_t) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, _t.S(2)), Compact = _compact, Cursor = Cursors.Hand };
            _tips.SetToolTip(_lcd, "Click to open the W7 control head.");
            _lcd.Click += delegate { ShowTool("HEAD"); };
            panel.Controls.Add(_lcd, 0, 0);
            var ind = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = _t.Back, Margin = new Padding(0), Padding = new Padding(0, _t.S(3), 0, 0) };
            foreach (string name in new string[] { "TX", "RX", "MON", "SCAN", "DIR", "LPWR", "SECURE", "EMERG", "BUS", "TRC", "REC", "FOOT" })
            {
                Label l = _t.MakeLabel(name, _t.Small, _t.Dim); l.Size = new Size(_t.S(name.Length > 4 ? (_compact ? 56 : 62) : (_compact ? 42 : 48)), _t.S(_compact ? 20 : 22)); l.TextAlign = ContentAlignment.MiddleCenter;
                l.BackColor = _t.Panel; l.Margin = new Padding(0, 0, _t.S(4), 0); ind.Controls.Add(l); _indicators[name] = l;
                l.Click += delegate { if (l.Text.EndsWith("?")) SyncRadioStatus(false); };
                _tips.SetToolTip(l, name + ": lit when the radio reports it on. \"" + name + "?\" (grey) = not reported yet: the XTL only reports a light when it changes, so the console does not guess. It clears by itself the first time the radio reports it (press the function, or change channel for DIR). Click a \"?\" light (or Tools > Sync radio status) to have the radio report it now.");
            }
            _tips.SetToolTip(_indicators["REC"], "REC: lit while receive audio is being recorded (green = also saved to disk). TX is always recorded while you transmit.");
            _tips.SetToolTip(_indicators["FOOT"], "FOOT: lit while the footswitch is pressed. \"FOOT?\" = the footswitch cannot be read (unplugged, wrong controller or COM port); the journal says why.");
            _tips.SetToolTip(_indicators["BUS"], "BUS: SB9600 link to the radio is open (grey in Test mode).");
            _tips.SetToolTip(_indicators["TRC"], "TRC: green = tone-remote audio to the radio is open and ready; amber = open, ARM TX needed (RequireArm); dark = not open (check Audio > TX routing).");
            panel.Controls.Add(ind, 0, 1);
            var channelRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = _t.Back, Margin = new Padding(0) };
            _zone = Combo(_t.S(_compact ? 96 : 120)); _channel = Combo(_t.S(_compact ? 230 : 330));
            _channel.FormattingEnabled = true;
            _channel.Format += delegate(object sender, ListControlConvertEventArgs e)
            {
                var c = e.ListItem as ChannelProfile; if (c == null) return;
                e.Value = (c.ZonePosition > 0 ? c.ZonePosition + "   " : "") + c.Name + (c.RxOnly ? "   (RX only)" : "");
            };
            _zone.SelectedIndexChanged += delegate { if (_refreshing || _zone.SelectedItem == null) return; int i = _selected.Channels.FindIndex(c => c.Zone == _zone.Text); ChangeChannel(i); };
            _channel.SelectedIndexChanged += delegate { if (!_refreshing && _channel.SelectedItem is ChannelProfile) ChangeChannel(_selected.Channels.IndexOf((ChannelProfile)_channel.SelectedItem)); };
            channelRow.Controls.Add(_zone); channelRow.Controls.Add(_channel);
            HotkeySettings hk = _config.Hotkeys;
            channelRow.Controls.Add(HotkeyButton("CH -", hk.ChannelDown, delegate { StepChannel(-1); }, 86, "Previous channel"));
            channelRow.Controls.Add(HotkeyButton("CH +", hk.ChannelUp, delegate { StepChannel(1); }, 86, "Next channel"));
            Button home = _t.MakeButton("HOME", delegate { ChangeChannel(_selected.HomeChannel, true); }, 74); channelRow.Controls.Add(home);
            _tips.SetToolTip(home, "Select the home channel (CPS import: Z1/2 STATE FD).");
            // Radio stepping only means something with more than one radio (the keyboard keys still work either way).
            if (_config.Radios.Count > 1)
            {
                channelRow.Controls.Add(HotkeyButton("< RADIO", hk.PreviousRadio, delegate { SelectRelative(-1); }, 96, "Select the previous radio"));
                channelRow.Controls.Add(HotkeyButton("RADIO >", hk.NextRadio, delegate { SelectRelative(1); }, 96, "Select the next radio"));
            }
            channelRow.Controls.Add(HotkeyButton("REPLAY", hk.ReplayLast, delegate { ReplayLast(1); }, 90, "Replay the last received call"));
            panel.Controls.Add(channelRow, 0, 2);
            panel.Controls.Add(BuildOperations(), 0, 3);
            return panel;
        }

        private void SelectTab(string name) { ShowTool(name); }

        private ComboBox Combo(int width)
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Font = _t.Bold, Margin = new Padding(_t.S(3), _t.S(6), _t.S(3), _t.S(3)), FlatStyle = FlatStyle.Flat, BackColor = _t.Button, ForeColor = _t.Text };
        }
        private FlowLayoutPanel Stack()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(_t.S(8)), BackColor = _t.Panel };
        }
        private FlowLayoutPanel Row(int width)
        {
            return new FlowLayoutPanel { Width = _t.S(width), Height = _t.ButtonH + _t.S(8), WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
        }
        // A button whose caption shows its keyboard shortcut; clicking it and pressing the key run the same method.
        private Button HotkeyButton(string caption, string hotkey, Action action, int width, string tip)
        {
            Keys k = Hotkey.Parse(hotkey);
            Button b = _t.MakeButton(k != Keys.None ? caption + "\n" + Hotkey.Format(k) : caption, action, width);
            if (k != Keys.None) b.Height = _t.ButtonH + _t.S(8);
            b.Tag = k; _tips.SetToolTip(b, tip + (k != Keys.None ? "  -  keyboard " + Hotkey.Format(k) : ""));
            return b;
        }
        private Label Heading(string text, int width) { Label l = _t.MakeLabel(text, _t.Bold, _t.Warn); l.Size = new Size(_t.S(width), _t.S(24)); return l; }
        private Label Note(string text, int width, int height) { Label l = _t.MakeLabel(text, _t.Small, _t.Dim); l.Size = new Size(_t.S(width), _t.S(height)); l.TextAlign = ContentAlignment.TopLeft; return l; }

        private Control BuildPttBar()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, BackColor = _t.Back, Padding = new Padding(0, _t.S(4), 0, _t.S(4)), Margin = new Padding(0) };
            // GROUP PTT and ALL CALL only differ from HOLD TO TALK with several radios, so one radio shows just HOLD TO TALK.
            bool several = _config.Radios.Count > 1; int side = _t.S(_compact ? 124 : 150);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, several ? 24 : 36)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, several ? 14 : 0)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, several ? 12 : 0));
            // ARM TX only appears when Settings > Console settings > Operation > RequireArm is on (bench testing).
            bool arm = _config.RequireArm;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, several ? 50 : 64)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, arm ? side : 0)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, side));
            _ptt = PttButton("HOLD TO TALK\n" + Hotkey.Display(_config.Hotkeys.Ptt), "SELECTED"); _groupPtt = PttButton("GROUP PTT" + (Hotkey.Parse(_config.Hotkeys.GroupPtt) != Keys.None ? "\n" + Hotkey.Display(_config.Hotkeys.GroupPtt) : ""), "GROUP"); _allPtt = PttButton("ALL CALL", "ALL");
            row.Controls.Add(_ptt, 0, 0); row.Controls.Add(_groupPtt, 1, 0); row.Controls.Add(_allPtt, 2, 0);
            _groupPtt.Visible = several; _allPtt.Visible = several;
            _txStatus = _t.MakeLabel("", _t.Mono, _t.Accent); _txStatus.Dock = DockStyle.Fill; _txStatus.TextAlign = ContentAlignment.MiddleCenter; _txStatus.Font = _t.Big;
            row.Controls.Add(_txStatus, 3, 0);
            _arm = _t.MakeButton("TX SAFE", delegate { ToggleArm(); }, 140); _arm.Dock = DockStyle.Fill; _arm.Font = _t.Big;
            _tips.SetToolTip(_arm, "Bench-test safety (Settings > Console settings > RequireArm): ARM TX before any tone or voice is sent to the consolette."); _arm.Visible = arm;
            row.Controls.Add(_arm, 4, 0);
            Button stop = _t.MakeButton("STOP ALL\n" + Hotkey.Display(_config.Hotkeys.StopAll).ToUpperInvariant(), delegate { StopAll("STOP ALL"); }, 140); stop.Dock = DockStyle.Fill; _t.StyleButton(stop, _t.Bad); stop.Font = _t.Big;
            row.Controls.Add(stop, 5, 0);
            return row;
        }

        private Button PttButton(string caption, string scope)
        {
            Button b = _t.MakeButton(caption, null, 160); b.Dock = DockStyle.Fill; b.Font = _t.Big; _t.StyleButton(b, PttIdle); b.ForeColor = Color.White;
            b.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { b.Capture = true; StartPtt(scope); } };
            b.MouseUp += delegate { if (IsHoldPtt()) StopTx("PTT released"); b.Capture = false; };
            b.MouseCaptureChanged += delegate { if (!b.Capture && IsHoldPtt() && _heldPttKey == Keys.None) StopTx("PTT capture released"); };
            _tips.SetToolTip(b, "Hold to transmit (or hold " + Hotkey.Display(scope == "GROUP" ? _config.Hotkeys.GroupPtt : _config.Hotkeys.Ptt) + "). Release, focus loss, " + Hotkey.Display(_config.Hotkeys.StopAll) + " or the " + _config.PttTimeoutSeconds + " s timeout ends TX.");
            return b;
        }
        private bool IsHoldPtt() { return _tx.Active && _tx.Reason == "PTT"; }
        // PTT buttons are bright red in every theme; brighter still (white text) while on the air.
        internal static readonly Color PttIdle = Color.FromArgb(205, 18, 18), PttOnAir = Color.FromArgb(255, 72, 72);

        private Control BuildJournal()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _t.Panel, Margin = new Padding(0) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(20))); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label title = _t.MakeLabel("  ACTIVITY / OPERATOR JOURNAL", _t.Small, _t.Accent); title.Dock = DockStyle.Fill; panel.Controls.Add(title, 0, 0);
            _events = new ListBox { Dock = DockStyle.Fill, BackColor = _t.Panel, ForeColor = _t.Text, BorderStyle = BorderStyle.None, Font = _t.Mono, IntegralHeight = false };
            foreach (string line in _history.Skip(Math.Max(0, _history.Count - 300))) _events.Items.Add(line);
            if (_events.Items.Count > 0) _events.TopIndex = _events.Items.Count - 1;
            panel.Controls.Add(_events, 0, 1);
            panel.Visible = _config.Ui.ShowJournal;
            return panel;
        }

        // =====================================================================
        // Quick keys (operator-customisable macro buttons)
        // =====================================================================
        private void RebuildQuickKeys()
        {
            if (_quick == null) return;
            _quick.SuspendLayout();
            foreach (Control old in _quick.Controls.Cast<Control>().ToArray()) old.Dispose();
            _quick.Controls.Clear();
            _quickLook.Clear();
            for (int i = 0; i < _config.QuickKeys.Count; i++)
            {
                QuickKey key = _config.QuickKeys[i]; int index = i;
                // The caption shows the key exactly as the keyboard dispatcher understands it, so the two always agree.
                Keys bound = Hotkey.Parse(key.Hotkey); string hotText = bound != Keys.None ? Hotkey.Format(bound) : (String.IsNullOrWhiteSpace(key.Hotkey) ? "" : "?? " + key.Hotkey);
                Button b = _t.MakeButton(key.Label + (hotText.Length == 0 ? "" : "\n" + hotText), delegate { RunAction(key.Action, "QUICK " + key.Label); }, 100);
                b.Font = _t.Bold; b.Name = "QK" + i; b.Tag = bound;
                if (!String.IsNullOrEmpty(key.ColorHex)) _t.StyleButton(b, UiSettings.ParseHex(key.ColorHex, _t.Button));
                _tips.SetToolTip(b, key.Action + (bound != Keys.None ? "\nKeyboard: " + hotText : hotText.Length > 0 ? "\nHOTKEY NOT RECOGNISED - edit this key" : "") + "\n(right-click to edit)");
                var menu = new ContextMenuStrip();
                menu.Items.Add("Edit key...", null, delegate { EditQuickKey(index); });
                menu.Items.Add("Move left", null, delegate { MoveQuickKey(index, -1); });
                menu.Items.Add("Move right", null, delegate { MoveQuickKey(index, 1); });
                menu.Items.Add("Duplicate", null, delegate { if (_config.QuickKeys.Count >= 64) { Log("At most 64 quick keys."); return; } var copy = new QuickKey(key.Label, key.Action, key.ColorHex, ""); _config.QuickKeys.Insert(index + 1, copy); QuickKeysChanged(); });
                menu.Items.Add("Delete", null, delegate { _config.QuickKeys.RemoveAt(index); QuickKeysChanged(); });
                b.ContextMenuStrip = menu;
                _quick.Controls.Add(b);
            }
            Button add = _t.MakeButton("+", delegate { EditQuickKey(-1); }, 58); add.Font = _t.Big;
            _tips.SetToolTip(add, "Add a quick key. Actions: alert:NAME, chan:N, trc:MON, head:HOME, func:SCAN, select:RADIO, dtmf:123, sb:05 61 02 57, wait:200 ... join steps with ;");
            _quick.Controls.Add(add);
            _quick.ResumeLayout();
            LayoutQuickKeys();
        }
        private void LayoutQuickKeys()
        {
            if (_quick == null || _quick.IsDisposed) return;
            int m = _t.S(3), addW = _t.S(58);
            int avail = _quick.ClientSize.Width - _t.S(4) - (_quick.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
            int cols = QuickColumns(avail);
            int keyW = Math.Max(_t.S(64), (avail - (addW + 2 * m)) / cols - 2 * m);
            for (int i = 0; i < _quick.Controls.Count; i++)
            {
                Control c = _quick.Controls[i]; c.Height = QuickKeyHeight(); c.Margin = new Padding(m);
                c.Width = i == _quick.Controls.Count - 1 ? addW : keyW;
            }
            // The bar grows / shrinks with the number of rows the keys need at this width (up to 3, Compact 2; then it scrolls).
            var root = _rootControl as TableLayoutPanel;
            if (root != null && root.RowStyles.Count > 3 && _config.Ui.ShowQuickKeys)
            {
                int rows = Math.Min(_compact ? 2 : 3, Math.Max(1, (_config.QuickKeys.Count + cols - 1) / cols));
                float want = rows * (QuickKeyHeight() + _t.S(6)) + _t.S(4);
                if (Math.Abs(root.RowStyles[3].Height - want) > 0.5f) { root.RowStyles[3].Height = want; FitCompactRows(); }
            }
        }
        // A quick key that runs a radio function (func:MON, func:SCAN, trc:MON ...) lights while the radio reports it on,
        // and is greyed in LIVE when the radio has no key for it - the same feedback the old function buttons gave.
        private readonly Dictionary<int, int> _quickLook = new Dictionary<int, int>();
        internal static string QuickFunction(string action)
        {
            string a = (action ?? "").Trim(); if (a.Contains(";")) return null;
            if (a.StartsWith("func:", StringComparison.OrdinalIgnoreCase)) return a.Substring(5).Trim().ToUpperInvariant();
            if (String.Equals(a, "trc:MON", StringComparison.OrdinalIgnoreCase)) return "MON";
            return null;
        }
        internal static bool FunctionOn(string function, RadioState state)
        {
            switch (function)
            {
                case "SCAN": return state.Scan;
                case "MON": return state.Monitor;
                case "DIRECT": return state.Direct;
                case "LOW PWR": return state.LowPower;
                case "SECURE": return state.Secure;
                case "BEACON": return state.Beacon;
                case "ACK EMERG": return state.Emergency;
                default: return false;
            }
        }
        private void RefreshQuickKeyStates(RadioState state)
        {
            if (_quick == null || _quick.IsDisposed) return;
            for (int i = 0; i < _config.QuickKeys.Count && i < _quick.Controls.Count; i++)
            {
                QuickKey key = _config.QuickKeys[i]; Button b = _quick.Controls[i] as Button;
                string fn = QuickFunction(key.Action); if (b == null || b.Name != "QK" + i || fn == null) continue;
                bool usable = _opMode == OpMode.Simulation || FunctionUsable(fn), on = FunctionOn(fn, state);
                int look = (usable ? 1 : 0) + (on ? 2 : 0), was;
                if (_quickLook.TryGetValue(i, out was) && was == look) continue;
                _quickLook[i] = look;
                if (b.Enabled != usable) b.Enabled = usable;
                _t.StyleButton(b, on ? _t.Active : !String.IsNullOrEmpty(key.ColorHex) ? UiSettings.ParseHex(key.ColorHex, _t.Button) : _t.Button);
            }
        }
        private void MoveQuickKey(int index, int delta)
        {
            int target = index + delta; if (target < 0 || target >= _config.QuickKeys.Count) return;
            QuickKey k = _config.QuickKeys[index]; _config.QuickKeys.RemoveAt(index); _config.QuickKeys.Insert(target, k); QuickKeysChanged();
        }
        private void EditQuickKey(int index)
        {
            QuickKey key = index >= 0 ? _config.QuickKeys[index] : new QuickKey("NEW KEY", "alert:" + _config.Alerts[0].Name, "", "");
            using (var dialog = new QuickKeyDialog(key, _config, _selected))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (index < 0) { if (_config.QuickKeys.Count >= 64) { Log("At most 64 quick keys."); return; } _config.QuickKeys.Add(dialog.Result); }
                else _config.QuickKeys[index] = dialog.Result;
                QuickKeysChanged();
            }
        }
        private void QuickKeysChanged() { StopAll("Layout changed"); Save(); BuildUi(); ReportHotkeyProblems(); }

        // =====================================================================
        // Radio cards
        // =====================================================================
        private void RebuildCards()
        {
            if (_cards == null) return;
            _cards.SuspendLayout();
            foreach (Control old in _cards.Controls.Cast<Control>().ToArray()) old.Dispose();
            _cards.Controls.Clear(); _cardControls.Clear();
            foreach (RadioProfile radio in _config.Radios)
            {
                RadioProfile resource = radio;
                var card = new RadioCard(_t, radio, _states[radio.Id], CardHeight());
                card.Selected = delegate { SelectRadio(resource); };
                card.Group.CheckedChanged += delegate { _states[resource.Id].Group = card.Group.Checked; };
                card.Mute.Click += delegate { ToggleMute(resource); };
                var menu = new ContextMenuStrip();
                menu.Items.Add("Select", null, delegate { SelectRadio(resource); });
                menu.Items.Add("Rename...", null, delegate { RenameRadio(resource); });
                menu.Items.Add("Radio settings...", null, delegate { EditConfig(_config.Radios.IndexOf(resource)); });
                menu.Items.Add("Live RX audio panel...", null, delegate { SelectRadio(resource); OpenAudio(); });
                menu.Items.Add("Bus monitor", null, delegate { SelectRadio(resource); OpenBusMonitor(); });
                menu.Items.Add("-");
                menu.Items.Add("Move up", null, delegate { MoveRadio(resource, -1); });
                menu.Items.Add("Move down", null, delegate { MoveRadio(resource, 1); });
                menu.Items.Add("Remove radio...", null, delegate { RemoveRadio(resource); });
                card.ContextMenuStrip = menu;
                _cards.Controls.Add(card); _cardControls[radio.Id] = card;
            }
            _cards.ResumeLayout();
            LayoutCards(); RefreshCards();
        }
        private void LayoutCards()
        {
            if (_cards == null || _cards.IsDisposed) return;
            int width;
            if (_config.Ui.Placement == CardPlacement.Left && !_compact)
            {
                int cols = Math.Max(1, _config.Ui.CardColumns);
                int avail = _cards.ClientSize.Width - _t.S(10) - (_cards.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
                width = Math.Max(_t.S(170), avail / cols - _t.S(6));
            }
            else width = _t.S(300);
            foreach (Control c in _cards.Controls) c.Width = width;
        }
        private void RefreshCards()
        {
            foreach (RadioProfile radio in _config.Radios)
            {
                RadioCard card; if (!_cardControls.TryGetValue(radio.Id, out card)) continue;
                RadioState state = _states[radio.Id]; RadioLink link = Link(radio);
                ChannelProfile ch = radio.Channels[radio.SelectedChannel];
                card.IsSelected = radio == _selected; card.Transmitting = _tx.Targets.Contains(radio.Id) || state.BusTx; card.Receiving = state.Rx || state.BusRx;
                card.Muted = radio.Muted; card.Flash = _pulse % 10 < 5; card.Live = link != null && (link.BusOpen || link.TrcOpen);
                card.ChannelText = (ch.ZonePosition > 0 ? "Z" + ch.ZoneNumber + "/" + ch.ZonePosition : "CH " + (radio.SelectedChannel + 1)) + "  " + ch.Name + (ch.RxOnly ? "  (RX)" : "");
                card.ZoneText = ch.Zone; card.StatusText = (radio.Muted ? "MUTED" : "VOL " + radio.RxVolume) + (state.Scan ? "  SCAN" : "") + (state.Monitor ? "  MON" : "");
                card.LinkText = link == null ? "" : link.Summary(_opMode);
                AudioPane pane; double level = 0;
                if (card.Transmitting && link != null && link.Trc != null) level = Math.Max(0, (20 * Math.Log10(Math.Max(1, link.Trc.OutPeak) / 32768.0) + 60) / 60.0);
                else if (_audioPanes.TryGetValue(radio.Id, out pane) && pane.Monitoring) level = Math.Max(0, (20 * Math.Log10(Math.Max(1, pane.LivePeak) / 32768.0) + 60) / 60.0);
                else if (_opMode == OpMode.Simulation && card.Receiving) level = 0.3 + (_pulse * 7 % 40) / 100.0;
                card.Level = level;
                if (card.Group.Checked != state.Group) card.Group.Checked = state.Group;
                card.Invalidate();
            }
        }
        private void SelectRadio(RadioProfile radio)
        {
            if (radio == _selected) return;
            if (_tx.Active && _tx.Reason == "PTT") { _queueRunning = false; StopTx("Resource selection changed"); }
            _selected = radio; RefreshSelected();
        }
        private void SelectRelative(int delta)
        {
            int i = _config.Radios.IndexOf(_selected); int n = _config.Radios.Count;
            SelectRadio(_config.Radios[((i + delta) % n + n) % n]);
        }
        private void MoveRadio(RadioProfile radio, int delta)
        {
            int i = _config.Radios.IndexOf(radio), j = i + delta; if (j < 0 || j >= _config.Radios.Count) return;
            _config.Radios.RemoveAt(i); _config.Radios.Insert(j, radio); Save(); RebuildCards();
        }
        private void RenameRadio(RadioProfile radio)
        {
            using (var dialog = new LabelDialog("RADIO NAME", radio.Name))
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.ResultText.Trim().Length > 0) { radio.Name = dialog.ResultText.Trim(); Save(); RebuildCards(); RefreshSelected(); }
        }
        private void RemoveRadio(RadioProfile radio)
        {
            if (_config.Radios.Count < 2) { Log("At least one radio must remain."); return; }
            if (MessageBox.Show(this, "Remove " + radio.Name + " and its settings?", "Remove radio", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            StopAll("Radio removed"); CloseLink(radio.Id);
            _config.Radios.Remove(radio); _states.Remove(radio.Id);
            if (_selected == radio) _selected = _config.Radios[0];
            Save(); RebuildCards(); RefreshSelected();
        }

        // =====================================================================
        // Selected radio display
        // =====================================================================
        private void RefreshSelected()
        {
            _refreshing = true;
            try
            {
                ChannelProfile current = _selected.Channels[_selected.SelectedChannel];
                if (_zone != null)
                {
                    _zone.Items.Clear(); foreach (string zone in _selected.Channels.Select(c => c.Zone).Distinct()) _zone.Items.Add(zone); _zone.SelectedItem = current.Zone;
                    _channel.Items.Clear(); foreach (ChannelProfile channel in _selected.Channels.Where(c => c.Zone == current.Zone)) _channel.Items.Add(channel); _channel.SelectedItem = current;
                }
                if (_presets != null)
                {
                    object keep = _presets.SelectedItem;
                    _presets.Items.Clear(); foreach (AlertProfile alert in _config.Alerts) _presets.Items.Add(alert);
                    if (keep != null && _presets.Items.Contains(keep)) _presets.SelectedItem = keep; else if (_presets.Items.Count > 0) _presets.SelectedIndex = 0;
                }
                if (_alertButtons != null)
                {
                    foreach (Control old in _alertButtons.Controls.Cast<Control>().ToArray()) old.Dispose(); _alertButtons.Controls.Clear();
                    foreach (AlertProfile alert in _config.Alerts.Where(a => a.ShowOnMain).Take(32))
                    {
                        AlertProfile preset = alert;
                        Button b = _t.MakeButton(alert.Name, delegate { _presets.SelectedItem = preset; SendAlert(); }, 170);
                        _t.StyleButton(b, !String.IsNullOrEmpty(alert.ColorHex) ? UiSettings.ParseHex(alert.ColorHex, _t.Button) : UiTheme.Mix(_t.Button, _t.Warn, 0.35));
                        _tips.SetToolTip(b, alert.Kind == AlertKind.TwoTone ? "Tone set: " + alert.FrequencyA + " Hz for " + alert.ToneAMs + " ms, then " + alert.FrequencyB + " Hz for " + alert.ToneBMs + " ms" : alert.Kind + " alert");
                        _alertButtons.Controls.Add(b);
                    }
                }
                if (_softButtons != null)
                {
                    foreach (Control old in _softButtons.Controls.Cast<Control>().ToArray()) old.Dispose(); _softButtons.Controls.Clear();
                    foreach (SoftKeyProfile key in _selected.Softkeys)
                    {
                        SoftKeyProfile binding = key;
                        Button b = _t.MakeButton(key.Label, delegate { RunAction(binding.Binding, "SOFTKEY " + binding.Label); }, 104);
                        b.Tag = binding; _tips.SetToolTip(b, "Action: " + key.Binding); _softButtons.Controls.Add(b);
                    }
                }
                if (_headButtons != null)
                {
                    foreach (Control old in _headButtons.Controls.Cast<Control>().ToArray()) old.Dispose(); _headButtons.Controls.Clear();
                    foreach (HeadButton hb in _selected.Bus.HeadButtons)
                    {
                        HeadButton button = hb;
                        Button b = _t.MakeButton(hb.Name, delegate { HeadPress(button.Name, "click"); }, 86);
                        _tips.SetToolTip(b, "SB9600 control 0x" + hb.Code.ToString("X2") + (String.IsNullOrEmpty(hb.Note) ? "" : " - " + hb.Note) + ". Right-click for press/hold.");
                        var menu = new ContextMenuStrip();
                        menu.Items.Add("Toggle (single 0x02 message)", null, delegate { HeadPress(button.Name, "toggle"); });
                        menu.Items.Add("Press (down)", null, delegate { HeadPress(button.Name, "down"); });
                        menu.Items.Add("Release (up)", null, delegate { HeadPress(button.Name, "up"); });
                        b.ContextMenuStrip = menu;
                        _headButtons.Controls.Add(b);
                    }
                }
                if (_channelKeys != null)
                {
                    foreach (Control old in _channelKeys.Controls.Cast<Control>().ToArray()) old.Dispose(); _channelKeys.Controls.Clear();
                    int w = ChannelKeyWidth();
                    bool zoned = _selected.Channels.All(c => c.ZoneNumber > 0);
                    int viewZone = zoned ? (_viewZone > 0 && _viewZoneRadio == _selected.Id ? _viewZone : current.ZoneNumber) : 0;
                    if (zoned)
                    {
                        List<int> zones = _selected.Channels.Select(c => c.ZoneNumber).Distinct().OrderBy(n => n).ToList();
                        Button last = null;
                        foreach (int z in zones)
                        {
                            int zz = z; string id = _selected.Id;
                            Button zb = _t.MakeButton("ZONE " + z + "  (" + _selected.Channels.Count(c => c.ZoneNumber == zz) + ")", delegate { _viewZone = zz; _viewZoneRadio = id; RefreshSelected(); }, 118);
                            zb.Tag = "zone"; _t.StyleButton(zb, z == viewZone ? UiTheme.Mix(_t.Button, _t.Accent, 0.45) : _t.Raised);
                            _tips.SetToolTip(zb, "Show zone " + z + " channels" + (z == current.ZoneNumber ? " (radio is in this zone)" : ""));
                            _channelKeys.Controls.Add(zb); last = zb;
                        }
                        if (last != null) _channelKeys.SetFlowBreak(last, true);
                    }
                    for (int i = 0; i < _selected.Channels.Count && i < 600; i++)
                    {
                        int index = i; ChannelProfile ch = _selected.Channels[i];
                        if (zoned && ch.ZoneNumber != viewZone) continue;
                        // Second line: the radio label (only when it differs from the name, e.g. not "VTAC 11 VTAC 11") + flags.
                        string label = (ch.RadioText ?? "").Trim();
                        if (String.Equals(System.Text.RegularExpressions.Regex.Replace(label, @"\s+", ""), System.Text.RegularExpressions.Regex.Replace(ch.Name ?? "", @"\s+", ""), StringComparison.OrdinalIgnoreCase)) label = "";
                        var parts = new List<string>();
                        if (label.Length > 0) parts.Add(label);
                        if (ch.ScanPriority > 0) parts.Add("SCAN P" + ch.ScanPriority); else if (ch.ScanMember) parts.Add("SCAN");
                        if (ch.RxOnly) parts.Add("RX ONLY");
                        if (i == _selected.HomeChannel) parts.Add("HOME");
                        var b = new ChannelKeyButton(_t) { Title = (zoned ? ch.ZonePosition : i + 1) + "  " + ch.Name, Sub = String.Join("  \u00B7  ", parts.ToArray()) };
                        b.Click += delegate { ChangeChannel(index); };
                        b.Width = w; b.Height = ChannelKeyButton.PreferredHeight(_t); b.Margin = new Padding(_t.S(3)); b.Tag = i;
                        b.AccessibleName = b.Title + (b.Sub.Length > 0 ? ", " + b.Sub : "");
                        _t.StyleButton(b, _t.Button);
                        if (i == _selected.SelectedChannel) _t.StyleButton(b, _t.Active);
                        else if (ch.RxOnly) _t.StyleButton(b, UiTheme.Mix(_t.Button, _t.Panel, 0.55));
                        _tips.SetToolTip(b, ch.Zone + " / " + ch.Name + (ch.FreqText.Length > 0 ? "\n" + ch.FreqText : "") + (ch.Personality > 0 ? "\nPersonality " + ch.Personality : "") + (ch.RxOnly ? "\nRECEIVE ONLY - the console will not key this channel" : ""));
                        _channelKeys.Controls.Add(b);
                    }
                }
                if (_sliders.Count > 0)
                {
                    _sliders["RX MONITOR"].Value = _selected.RxVolume; _sliders["MIC / VOICE"].Value = _selected.MicLevel;
                    _sliders["ALERT / PAGING"].Value = _selected.AlertLevel; _sliders["TONE REMOTE"].Value = _selected.ToneLevel;
                    foreach (var item in _sliders) _sliderLabels[item.Key].Text = item.Value.Value + "%";
                }
                if (_mixerNote != null)
                    _mixerNote.Text = "RX mute on TX: " + (_selected.MuteRxOnTx ? "ON" : "OFF") + "   |   Mic notch at " + _selected.Tone.GuardHz + " Hz: " + (_selected.MicNotch ? "ON" : "OFF") + "   |   PTT via " + (_selected.Ptt == PttMethod.TrcTones ? "TRC tones" : "SB9600 PTT button") + "\nTX output: " + Blank(_selected.PlannedTxOutput) + "\nMicrophone: " + Blank(_selected.MicrophoneDevice);
            }
            finally { _refreshing = false; }
            RefreshDisplay(); RefreshCards();
        }
        private static string Blank(string s) { return String.IsNullOrEmpty(s) ? "(not set - Settings > Console settings > radio tab > Audio)" : s; }

        private void RefreshDisplay()
        {
            if (_lcd == null) return;
            RadioState state = _states[_selected.Id]; ChannelProfile channel = _selected.Channels[_selected.SelectedChannel];
            RadioLink link = Link(_selected);
            _lcd.TopLeft = _selected.Name.ToUpperInvariant() + "  /  " + _selected.Model;
            _lcd.TopRight = _opMode == OpMode.Simulation ? "TEST MODE - SIMULATED RADIO" : link == null || !link.BusOpen ? "NO BUS READBACK - CHANNEL NOT CONFIRMED"
                : _seeks.ContainsKey(_selected.Id) ? "SB9600 " + link.Bus.PortLabel + " - CHANGING CHANNEL..."
                : state.RxChannel >= 0 ? "SB9600 " + link.Bus.PortLabel + (state.Scan ? " - SCAN RECEIVE" : " - RADIO ON ANOTHER CHANNEL")
                : "SB9600 " + link.Bus.PortLabel + (ChannelConfirmed(_selected) ? " - RADIO CONFIRMED" : " - NOT CONFIRMED BY RADIO");
            // Scan landing: the top display shows the channel the radio is receiving on; the keys stay on the operator's channel.
            ChannelProfile rxCh = state.RxChannel >= 0 && state.RxChannel < _selected.Channels.Count ? _selected.Channels[state.RxChannel] : null;
            _lcd.Main = state.MenuOpen ? "MENU: " + MenuOptions[state.MenuIndex] : state.DisplayMessage.Length > 0 ? state.DisplayMessage : rxCh != null ? rxCh.Name : channel.Name;
            if (channel.RxMHz > 0)
                _lcd.Detail = channel.Zone + " / CH " + channel.ZonePosition + "   |   " + channel.FreqText + (channel.Personality > 0 ? "   |   P" + channel.Personality : "") + (channel.RxOnly ? "   |   RX ONLY" : "") + (channel.ScanMember ? "   |   SCAN" + (channel.ScanPriority > 0 ? " P" + channel.ScanPriority : "") : "");
            else
                _lcd.Detail = channel.Zone + "   |   CH " + (_selected.SelectedChannel + 1) + "   |   FT " + channel.FunctionHz + " Hz   |   CTCSS " + (channel.CtcssHz == 0 ? "OFF" : channel.CtcssHz + " Hz") + (state.BusChannel >= 0 ? "   |   RADIO MODE " + state.BusChannel : "");
            if (rxCh != null)
                _lcd.Detail = (state.Scan ? "SCAN - RECEIVING ON " : "RADIO SHOWS ") + rxCh.Zone + " / CH " + rxCh.ZonePosition + (rxCh.FreqText.Length > 0 ? "   |   " + rxCh.FreqText : "") + "   |   YOUR CHANNEL: " + channel.Name;
            _lcd.Mirror = state.BusDisplay.Trim().Length > 0 ? "RADIO DISPLAY:  " + MirrorLine(state.BusDisplay) + (state.BusDisplay2.Trim().Length > 0 ? "  /  " + MirrorLine(state.BusDisplay2) : "") : "";
            _lcd.Alarm = state.Emergency; _lcd.Flash = _pulse % 10 < 5;
            _lcd.RightTop = state.Emergency ? "EMERGENCY - UNIT ID" : "LAST UNIT ID";
            _lcd.RightMain = state.Caller; _lcd.RightColor = state.Emergency ? _t.Bad : _t.Good;
            _lcd.Invalidate();
            RefreshQuickKeyStates(state);
            RefreshW7Keys();
            if (_w7Display != null) _w7Display.Text = state.BusDisplay.Trim().Length > 0 ? MirrorLine(state.BusDisplay) + (state.BusDisplay2.Trim().Length > 0 ? "\r\n" + MirrorLine(state.BusDisplay2) : "")
                : _opMode == OpMode.Simulation ? "TEST MODE\r\n" + ((channel.RadioText ?? "").Trim().Length > 0 ? channel.RadioText.Trim() + "  =  " : "") + channel.Name : "WAITING FOR\r\nSBEP DISPLAY";
            if (_headMirror != null) _headMirror.Text = "Radio display: " + (state.BusDisplay.Trim().Length > 0 ? MirrorLine(state.BusDisplay) + (state.BusDisplay2.Trim().Length > 0 ? " / " + MirrorLine(state.BusDisplay2) : "") : "(no SBEP display text yet)") + "     Indicators: " + IndicatorSummary(state);
            RefreshIndicators();
        }
        // "CITY FD  =  CITY FIRE DEPT" when the short radio text expands, otherwise just the radio text.
        private string MirrorLine(string raw)
        {
            string shortText = raw.Trim(), full = DisplayText.Expand(_selected, raw);
            return String.Equals(full, shortText, StringComparison.OrdinalIgnoreCase) ? shortText : shortText + "  =  " + full;
        }
        private static string IndicatorSummary(RadioState s)
        {
            if (s.Indicators.Count == 0) return "(none)";
            var parts = new List<string>(); foreach (var kv in s.Indicators) if (kv.Value != 0) parts.Add(SbDecoder.IndicatorName(kv.Key) + (kv.Value == 1 ? "" : "~"));
            return parts.Count == 0 ? "all off" : String.Join(" ", parts.ToArray());
        }
        private void RefreshIndicators()
        {
            if (_indicators.Count == 0) return;
            RadioState s = _states[_selected.Id]; RadioLink link = Link(_selected);
            bool tx = _tx.Targets.Contains(_selected.Id) || s.BusTx;
            // In LIVE with the bus open, latching states the radio has not reported yet show as "?" instead of a false "off".
            bool live = _opMode != OpMode.Simulation && link != null && link.BusOpen;
            Light("TX", tx, _t.Bad); Light("RX", s.Rx || s.BusRx, _t.Good); Light("MON", s.Monitor, _t.Accent, !live || s.Known.Contains("MON")); Light("SCAN", s.Scan, _t.Accent, !live || s.Known.Contains("SCAN"));
            Light("DIR", s.Direct, _t.Accent, !live || s.Known.Contains("DIR")); Light("LPWR", s.LowPower, _t.Accent, !live || s.Known.Contains("LPWR")); Light("SECURE", s.Secure, _t.Warn, !live || s.Known.Contains("SECURE")); Light("EMERG", s.Emergency && _pulse % 10 < 5, _t.Bad);
            // A light the radio can never report (no SBEP indicator code mapped for it) is hidden instead of showing a
            // permanent "?". Map a code in Settings > radio tab > SB9600 / RIB > Indicators to bring it back.
            ShowLight("DIR", _selected.Bus.IndicatorDirect >= 0); ShowLight("LPWR", _selected.Bus.IndicatorLowPower >= 0); ShowLight("SECURE", _selected.Bus.IndicatorSecure >= 0);
            Light("BUS", link != null && link.BusOpen, link != null && link.Bus.Simulated ? _t.Dim : _t.Good);
            // TRC: green = radio audio open and ready; amber = open but ARM TX is needed (RequireArm); dark = not open.
            Light("TRC", link != null && link.TrcOpen, _armed ? _t.Good : _t.Warn);
            AudioPane recPane; bool rxRecording = _audioPanes.TryGetValue(_selected.Id, out recPane) && recPane.Monitoring;
            Light("REC", rxRecording, _config.Recorder.DiskLogging ? _t.Good : _t.Warn);
            Light("FOOT", _footHeld, _t.Bad, _config.Footswitch.Source == FootswitchSource.Off || _foot == null || _foot.Ready);
            ShowLight("FOOT", _config.Footswitch.Source != FootswitchSource.Off);
        }
        private void ShowLight(string name, bool visible) { Label l; if (_indicators.TryGetValue(name, out l) && l.Visible != visible) l.Visible = visible; }
        private void Light(string name, bool on, Color color) { Light(name, on, color, true); }
        private void Light(string name, bool on, Color color, bool known)
        {
            Label l; if (!_indicators.TryGetValue(name, out l)) return;
            string text = known ? name : name + "?";
            // Not reported yet = dim grey with "?" (no warning colour): it is not a fault, the radio just has not said yet.
            Color back = known && on ? color : _t.Panel, fore = known && on ? UiTheme.ReadableOn(color) : _t.Dim;
            if (l.Text != text) l.Text = text;
            if (l.BackColor != back) l.BackColor = back;
            if (l.ForeColor != fore) l.ForeColor = fore;
        }

        // =====================================================================
        // Keyboard
        // =====================================================================
        private static Keys ParseKey(string text) { return Hotkey.Parse(text); }
        private bool TypingInTextBox() { return FocusedIsText(this); }
        internal static bool FocusedIsText(ContainerControl root)
        {
            Control c = root.ActiveControl; while (c is ContainerControl && ((ContainerControl)c).ActiveControl != null) c = ((ContainerControl)c).ActiveControl;
            return c is TextBoxBase;
        }
        // Keys that type something (letters, digits, PageUp...) without Ctrl/Alt; F-keys and Escape never count as typing.
        internal static bool IsPlainKey(Keys data)
        {
            Keys code = data & Keys.KeyCode;
            return (data & (Keys.Control | Keys.Alt)) == Keys.None && !(code >= Keys.F1 && code <= Keys.F24) && code != Keys.Escape;
        }

        // One dispatcher for every keyboard shortcut. It is called from ProcessCmdKey (main window and every tool window),
        // which runs before focused buttons, combo boxes and the menu bar can swallow the key, so a hotkey works no matter
        // what has focus. Each on-screen button with a hotkey runs the same method as its key. Returns true when handled.
        internal bool HandleHotkey(Keys data, bool repeat)
        {
            if (data == Keys.None) return false;
            HotkeySettings h = _config.Hotkeys; Keys code = data & Keys.KeyCode;
            if (IsPlainKey(data) && TypingInTextBox()) return false;   // let operators type digits, letters and PageUp in text boxes
            if (data == Hotkey.Parse(h.StopAll)) { if (!repeat) StopAll(Hotkey.Display(h.StopAll) + " / cancel"); return true; }
            if (data == Hotkey.Parse(h.Ptt) || data == Hotkey.Parse(h.GroupPtt))
            {
                if (_heldPttKey == Keys.None && !repeat) { _heldPttKey = code; StartPtt(data == Hotkey.Parse(h.Ptt) ? "SELECTED" : "GROUP"); }
                return true;
            }
            if (repeat && data != Hotkey.Parse(h.ChannelUp) && data != Hotkey.Parse(h.ChannelDown)) return QuickKeyIndex(data) >= 0 || IsGlobalHotkey(data);
            if (data == Hotkey.Parse(h.ChannelUp)) { StepChannel(1); return true; }
            if (data == Hotkey.Parse(h.ChannelDown)) { StepChannel(-1); return true; }
            if (data == Hotkey.Parse(h.NextRadio)) { SelectRelative(1); return true; }
            if (data == Hotkey.Parse(h.PreviousRadio)) { SelectRelative(-1); return true; }
            if (data == Hotkey.Parse(h.Monitor)) { Function("MON"); return true; }
            if (data == Hotkey.Parse(h.ReplayLast)) { ReplayLast(1); return true; }
            int q = QuickKeyIndex(data);
            if (q >= 0) { QuickKey key = _config.QuickKeys[q]; RunAction(key.Action, "HOTKEY " + Hotkey.Format(data) + " (" + key.Label + ")"); return true; }
            return false;
        }
        internal bool HandleHotkeyUp(Keys code)
        {
            if (_heldPttKey == Keys.None || code != _heldPttKey) return false;
            _heldPttKey = Keys.None;
            if (IsHoldPtt()) StopTx("PTT key released");
            return true;
        }
        private int QuickKeyIndex(Keys data)
        {
            for (int i = 0; i < _config.QuickKeys.Count; i++) if (Hotkey.Parse(_config.QuickKeys[i].Hotkey) == data) return i;
            return -1;
        }
        private bool IsGlobalHotkey(Keys data)
        {
            foreach (KeyValuePair<string, string> g in GlobalHotkeys()) if (Hotkey.Parse(g.Value) == data) return true;
            return false;
        }
        internal List<KeyValuePair<string, string>> GlobalHotkeys()
        {
            HotkeySettings h = _config.Hotkeys; var list = new List<KeyValuePair<string, string>>();
            list.Add(new KeyValuePair<string, string>("PTT (hold) - HOLD TO TALK", h.Ptt)); list.Add(new KeyValuePair<string, string>("Group PTT (hold) - GROUP PTT", h.GroupPtt));
            list.Add(new KeyValuePair<string, string>("Stop all - STOP ALL", h.StopAll)); list.Add(new KeyValuePair<string, string>("Channel up - CH +", h.ChannelUp));
            list.Add(new KeyValuePair<string, string>("Channel down - CH -", h.ChannelDown)); list.Add(new KeyValuePair<string, string>("Next radio - RADIO >", h.NextRadio));
            list.Add(new KeyValuePair<string, string>("Previous radio - < RADIO", h.PreviousRadio)); list.Add(new KeyValuePair<string, string>("Monitor (extra key; the MONITOR quick key also has one)", h.Monitor));
            list.Add(new KeyValuePair<string, string>("Replay last call - REPLAY", h.ReplayLast));
            return list;
        }
        // Unrecognised or duplicated hotkeys are reported once in the journal, so a key never silently does nothing.
        internal List<string> HotkeyProblems()
        {
            var problems = new List<string>(); var seen = new Dictionary<Keys, string>();
            var all = GlobalHotkeys();
            foreach (QuickKey k in _config.QuickKeys) all.Add(new KeyValuePair<string, string>("Quick key " + k.Label, k.Hotkey));
            foreach (KeyValuePair<string, string> item in all)
            {
                if (String.IsNullOrWhiteSpace(item.Value)) continue;
                Keys key = Hotkey.Parse(item.Value);
                if (key == Keys.None) { problems.Add(item.Key + ": '" + item.Value + "' is not a key name the console understands."); continue; }
                string other; if (seen.TryGetValue(key, out other)) problems.Add(item.Key + ": " + Hotkey.Format(key) + " is already used by " + other + " (that one wins)."); else seen[key] = item.Key;
                if (Hotkey.Reserved(key)) problems.Add(item.Key + ": " + Hotkey.Format(key) + " is reserved by Windows and may not reach the console.");
            }
            return problems;
        }
        private void ReportHotkeyProblems() { foreach (string p in HotkeyProblems()) Log("HOTKEY: " + p); }
        internal string HotkeyReference()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("KEYBOARD SHORTCUTS  (they work whichever control or console window has focus)"); sb.AppendLine();
            foreach (KeyValuePair<string, string> g in GlobalHotkeys()) sb.AppendLine("  " + (String.IsNullOrWhiteSpace(g.Value) ? "(none)" : Hotkey.Display(g.Value)).PadRight(16) + g.Key);
            sb.AppendLine(); sb.AppendLine("QUICK KEYS  (bottom bar - the same text is printed on each button)");
            foreach (QuickKey k in _config.QuickKeys) sb.AppendLine("  " + (String.IsNullOrWhiteSpace(k.Hotkey) ? "(none)" : Hotkey.Display(k.Hotkey)).PadRight(16) + k.Label + "   [" + k.Action + "]");
            sb.AppendLine(); sb.AppendLine("Letters, digits and PageUp/PageDown without Ctrl/Alt are left alone while you type in a text box.");
            sb.AppendLine("Change global keys in Settings > Console > Hotkeys; change quick keys by right-clicking the key > Edit.");
            List<string> problems = HotkeyProblems();
            if (problems.Count > 0) { sb.AppendLine(); sb.AppendLine("PROBLEMS"); foreach (string p in problems) sb.AppendLine("  " + p); }
            return sb.ToString();
        }
        private void OnKeyUpHandler(object sender, KeyEventArgs e) { if (HandleHotkeyUp(e.KeyCode)) e.SuppressKeyPress = true; }
        private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        internal static bool IsRepeat(ref Message msg) { return (msg.LParam.ToInt64() & 0x40000000L) != 0; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((msg.Msg == WM_KEYDOWN || msg.Msg == WM_SYSKEYDOWN) && HandleHotkey(keyData, IsRepeat(ref msg))) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // =====================================================================
        // Menus
        // =====================================================================
        private void ShowLayoutMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Appearance && layout...", null, delegate { EditAppearance(); });
            menu.Items.Add("-");
            foreach (string name in UiTheme.Names) { string theme = name; var item = new ToolStripMenuItem("Theme: " + name, null, delegate { _config.Ui.ThemeName = theme; ApplyUi(); }); item.Checked = _config.Ui.ThemeName == name; menu.Items.Add(item); }
            menu.Items.Add("-");
            foreach (int pct in new int[] { 90, 100, 115, 130, 150 }) { int p = pct; var item = new ToolStripMenuItem("Scale " + pct + "%", null, delegate { _config.Ui.FontScale = p; ApplyUi(); }); item.Checked = _config.Ui.FontScale == pct; menu.Items.Add(item); }
            menu.Items.Add("-");
            foreach (LayoutPreset preset in _config.Layouts) { LayoutPreset p = preset; menu.Items.Add("Load layout: " + preset.Name, null, delegate { LoadLayout(p); }); }
            menu.Items.Add("Save current layout as...", null, delegate { SaveLayoutAs(); });
            menu.Show(anchor, new Point(0, anchor.Height));
        }
        private void ShowViewMenu(Control anchor)
        {
            var menu = new ContextMenuStrip(); UiSettings ui = _config.Ui;
            AddToggle(menu, "Radio cards", ui.ShowCards, delegate { ui.ShowCards = !ui.ShowCards; });
            AddToggle(menu, "Cards on top (instead of left)", ui.Placement == CardPlacement.Top, delegate { ui.Placement = ui.Placement == CardPlacement.Top ? CardPlacement.Left : CardPlacement.Top; });
            AddToggle(menu, "Quick-key bar", ui.ShowQuickKeys, delegate { ui.ShowQuickKeys = !ui.ShowQuickKeys; });
            AddToggle(menu, "Journal", ui.ShowJournal, delegate { ui.ShowJournal = !ui.ShowJournal; });
            AddToggle(menu, "W7 control head on the main screen (Full layout)", ui.ShowHeadPanel, delegate { ui.ShowHeadPanel = !ui.ShowHeadPanel; });
            menu.Items.Add("-");
            foreach (LayoutMode m in new LayoutMode[] { LayoutMode.Auto, LayoutMode.Full, LayoutMode.Compact })
            {
                LayoutMode mm = m;
                AddToggle(menu, "Layout: " + (m == LayoutMode.Auto ? "Auto (fits the screen) - now " + (_compact ? "Compact" : "Full") : m == LayoutMode.Full ? "Full (large screens)" : "Compact (small screens, 1024x768)"), ui.Layout == m, delegate { ui.Layout = mm; });
            }
            menu.Items.Add("-");
            foreach (CardDensity d in new CardDensity[] { CardDensity.Compact, CardDensity.Normal, CardDensity.Large }) { CardDensity dd = d; AddToggle(menu, "Card size: " + d, ui.Density == d && !_config.CompactCards, delegate { ui.Density = dd; _config.CompactCards = false; }); }
            foreach (int c in new int[] { 1, 2, 3 }) { int cc = c; AddToggle(menu, "Card columns: " + c, ui.CardColumns == c, delegate { ui.CardColumns = cc; }); }
            menu.Items.Add("-");
            menu.Show(anchor, new Point(0, anchor.Height));
        }
        private void AddToggle(ContextMenuStrip menu, string text, bool on, Action change)
        {
            var item = new ToolStripMenuItem(text) { Checked = on };
            item.Click += delegate { change(); ApplyUi(); };
            menu.Items.Add(item);
        }
        private void ShowMoreMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Save configuration", null, delegate { Save(); });
            menu.Items.Add("Backup settings...", null, delegate { Backup(); });
            menu.Items.Add("Export journal...", null, delegate { ExportLog(); });
            menu.Items.Add("-");
            menu.Items.Add("Hardware setup guide (XTL5000 / TRC / RIB)...", null, delegate { ShowHardwareGuide(); });
            menu.Items.Add("Action reference (quick keys / softkeys)...", null, delegate { ShowActionReference(); });
            menu.Items.Add("Capabilities and limits...", null, delegate { Capabilities(); });
            menu.Items.Add("-");
            menu.Items.Add("About " + Brand.Product + "...", null, delegate { ShowAbout(); });
            menu.Show(anchor, new Point(0, anchor.Height));
        }
        private void ApplyUi()
        {
            try { _config.Ui.Validate(); } catch (Exception ex) { Log("Layout not applied: " + ex.Message); return; }
            StopAll("Layout changed"); Save(); BuildUi();
        }
        private void EditAppearance()
        {
            using (var dialog = new AppearanceDialog(_config.Ui.Copy()))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                UiSettings ui = dialog.Result; ui.WindowX = _config.Ui.WindowX; ui.WindowY = _config.Ui.WindowY; ui.WindowW = _config.Ui.WindowW; ui.WindowH = _config.Ui.WindowH; ui.Maximized = _config.Ui.Maximized;
                _config.Ui = ui; ApplyUi();
            }
        }
        private void LoadLayout(LayoutPreset preset)
        {
            UiSettings ui = preset.Ui.Copy(); ui.WindowX = _config.Ui.WindowX; ui.WindowY = _config.Ui.WindowY; ui.WindowW = _config.Ui.WindowW; ui.WindowH = _config.Ui.WindowH; ui.Maximized = _config.Ui.Maximized;
            _config.Ui = ui; ApplyUi(); Log("Layout loaded: " + preset.Name);
        }
        private void SaveLayoutAs()
        {
            using (var dialog = new LabelDialog("LAYOUT NAME", "My layout"))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ResultText.Trim().Length == 0) return;
                string name = dialog.ResultText.Trim();
                _config.Layouts.RemoveAll(l => l.Name == name);
                if (_config.Layouts.Count >= 32) { Log("At most 32 layouts."); return; }
                _config.Layouts.Add(new LayoutPreset { Name = name, Ui = _config.Ui.Copy() }); Save(); Log("Layout saved: " + name);
            }
        }

        // =====================================================================
        // Tick loop
        // =====================================================================
        private void Tick()
        {
            DateTime now = DateTime.UtcNow; _pulse++;
            if (_clock != null) _clock.Text = DateTime.Now.ToString("ddd dd MMM yyyy") + "\n" + DateTime.Now.ToString(_config.Ui.Use24Hour ? "HH:mm:ss" : "h:mm:ss tt");
            ProcessBus();
            ProcessSeeks();
            ProcessChecks();
            ProcessLayoutCheck();
            ProcessFootswitch();
            CheckBusPorts();
            ProcessSync();
            ProcessPurge();
            ProcessMacro();
            MonitorLinks();
            if (_tx.Expired(now))
            {
                bool timedOut = _tx.Reason == "PTT" || _opMode == OpMode.Live;
                StopTx(timedOut ? "TX timeout (" + _config.PttTimeoutSeconds + " s)" : "Timed completion");
                if (_queueRunning) NextPage();
            }
            if (_preview != null && _preview.Finished) StopPreview();
            if (_opMode == OpMode.Simulation && (now - _lastBeacon).TotalSeconds >= 5)
            {
                _lastBeacon = now;
                foreach (RadioProfile radio in _config.Radios.Where(r => _states[r.Id].Beacon)) Trace("SIM BEACON marker / " + radio.Name + " (no audio)");
            }
            _recorder.Poll();
            if (_recorder.LastError.Length > 0) { Log(_recorder.LastError); _recorder.LastError = ""; }
            if (_callsVersion != _recorder.Version) RefreshCalls();
            UpdateMeters();
            UpdateStatus(now);
            if (_pulse % 4 == 0) { RefreshCards(); RefreshIndicators(); if (_states[_selected.Id].Emergency) RefreshDisplay(); }
        }

        private void UpdateMeters()
        {
            RadioLink link = Link(_selected); bool tx = _tx.Targets.Contains(_selected.Id); RadioState state = _states[_selected.Id];
            AudioPane pane; bool live = _audioPanes.TryGetValue(_selected.Id, out pane) && pane.Monitoring;
            if (_rxMeter != null)
            {
                if (live) _rxMeter.SetPeak(pane.LivePeak);
                else if (_opMode == OpMode.Simulation && (state.Rx || state.BusRx) && !(tx && _selected.MuteRxOnTx)) _rxMeter.SetLevel(0.35 + (_pulse * 17 % 45) / 100.0);
                else _rxMeter.SetLevel(0);
            }
            if (_txMeter != null) _txMeter.SetPeak(link != null && link.Trc != null ? link.Trc.OutPeak : 0);
            if (_micMeter != null)
            {
                if (link != null && link.Trc != null && link.Trc.IsOpen) _micMeter.SetPeak(link.Trc.MicPeak);
                else _micMeter.SetLevel(_opMode == OpMode.Simulation && tx ? Math.Min(1, _selected.MicLevel / 100.0 * (0.4 + (_pulse * 11 % 50) / 100.0)) : 0);
            }
        }

        private void UpdateStatus(DateTime now)
        {
            if (_txStatus == null) return;
            RadioLink link = Link(_selected);
            string phase = link != null && link.Trc != null && link.Trc.IsOpen ? link.Trc.Phase : "";
            RadioState selState; bool dropped = _tx.Active && _tx.Targets.Contains(_selected.Id) && _states.TryGetValue(_selected.Id, out selState) && selState.RadioDropped;
            if (dropped)
                _txStatus.Text = "RADIO STOPPED TRANSMITTING\nPTT held - check TX levels (journal)";
            else if (_tx.Active)
                _txStatus.Text = (_opMode == OpMode.Live ? "ON AIR  " : "TEST TX  ") + (now - _tx.Started).TotalSeconds.ToString("0.0") + " s" + (phase.Length > 0 ? "  [" + phase + "]" : "") + "\n" + _tx.Reason + " -> " + String.Join(", ", _config.Radios.Where(r => _tx.Targets.Contains(r.Id)).Select(r => r.Name).ToArray());
            else if (link != null && link.Trc != null && link.Trc.Active)
                _txStatus.Text = "TONE OUTPUT  [" + phase + "]\n" + link.LastJob;
            else
                _txStatus.Text = _opMode == OpMode.Simulation ? "TEST MODE\nSimulated radio - nothing is sent" : _opMode == OpMode.LiveReceive ? "RECEIVE ONLY\nTransmit is off in this mode"
                    : !_armed ? "TX SAFE - press ARM TX\n" + (link != null && link.TrcOpen ? "TRC output open" : "TRC output not open")
                    : link != null && link.TrcOpen ? "READY" + (_config.RequireArm ? " - TX ARMED" : "") + "\nHold PTT or " + Hotkey.Display(_config.Hotkeys.Ptt) + " to talk" : "RADIO AUDIO NOT OPEN\nCheck Audio > TX routing";
            Color pttColor = _tx.Active ? PttOnAir : PttIdle;
            if (_ptt.BackColor != pttColor) { _t.StyleButton(_ptt, pttColor); _ptt.ForeColor = Color.White; }
            bool canTx = _opMode == OpMode.Simulation || (_opMode == OpMode.Live && _armed);
            _ptt.Enabled = _groupPtt.Enabled = _allPtt.Enabled = canTx || _tx.Active;
            _arm.Enabled = _opMode == OpMode.Live;
            string armText = _armed ? "TX ARMED" : "ARM TX"; if (_arm.Text != armText) { _arm.Text = armText; _t.StyleButton(_arm, _armed ? (_pulse % 10 < 5 ? _t.Bad : UiTheme.Mix(_t.Bad, _t.Panel, 0.4)) : _t.Button); }
            else if (_armed && _pulse % 5 == 0) _t.StyleButton(_arm, _pulse % 10 < 5 ? _t.Bad : UiTheme.Mix(_t.Bad, _t.Panel, 0.4));
            string badge; Color badgeColor;
            if (_opMode == OpMode.Simulation) { badge = "TEST MODE"; badgeColor = _t.Warn; }
            else if (dropped) { badge = "NOT ON AIR"; badgeColor = _pulse % 10 < 5 ? _t.Warn : _t.Bad; }
            else if (_tx.Active) { badge = "ON AIR"; badgeColor = _t.Bad; }
            else if (_opMode == OpMode.LiveReceive) { badge = "RECEIVE ONLY"; badgeColor = _t.Accent; }
            else if (!_armed) { badge = "LIVE  -  TX SAFE"; badgeColor = _t.Warn; }
            else { badge = _config.RequireArm ? "LIVE  -  TX ARMED" : "LIVE"; badgeColor = _config.RequireArm ? _t.Bad : _t.Good; }
            if (_modeBadge.Text != badge) { _modeBadge.Text = badge; _modeBadge.BackColor = badgeColor; _modeBadge.ForeColor = UiTheme.ReadableOn(badgeColor); }
            if (_pulse % 10 == 0)
            {
                var parts = new List<string>();
                parts.Add(_opMode == OpMode.Simulation ? "TEST MODE" : _opMode == OpMode.LiveReceive ? "RECEIVE ONLY" : _config.RequireArm ? "LIVE " + (_armed ? "ARMED" : "TX SAFE") : "LIVE");
                parts.Add(_config.OperatorName); parts.Add(_config.Radios.Count + " radio(s)");
                if (link != null) { parts.Add(link.BusText()); parts.Add(link.TrcText()); }
                parts.Add("Recall " + _recorder.Calls().Count + " call(s)" + (_config.Recorder.DiskLogging ? " + disk log" + (_config.Recorder.KeepDays > 0 ? " (" + _config.Recorder.KeepDays + " days)" : "") : ""));
                if (_lastCheck.Length > 0) parts.Add("Last command: " + _lastCheck);
                _footer.Text = "  " + String.Join("   |   ", parts.ToArray());
            }
        }

        // =====================================================================
        // Journal, trace, persistence
        // =====================================================================
        private void Trace(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text;
            _traceLines.Add(line); if (_traceLines.Count > 3000) _traceLines.RemoveRange(0, 500);
            if (_trace == null || _trace.IsDisposed) return;
            _trace.AppendText(line + Environment.NewLine);
            if (_trace.TextLength > 200000) { _trace.Text = _trace.Text.Substring(_trace.TextLength - 150000); _trace.SelectionStart = _trace.TextLength; }
        }
        private void Log(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text; _history.Add(line); if (_history.Count > 5000) _history.RemoveAt(0);
            if (_events == null || _events.IsDisposed) return;
            _events.Items.Add(line); while (_events.Items.Count > 400) _events.Items.RemoveAt(0); _events.TopIndex = _events.Items.Count - 1;
        }
        private bool Save()
        {
            try { _config.Save(_path); return true; }
            catch (Exception ex) { Log("Configuration save failed: " + ex.Message); return false; }
        }
        private void RestoreWindow()
        {
            UiSettings ui = _config.Ui; StartPosition = FormStartPosition.Manual;
            // The saved size is kept only if it fits the screen it was on (the console may move from a 1080p PC to a 1024x768 laptop).
            var saved = new Rectangle(ui.WindowX, ui.WindowY, Math.Max(MinimumSize.Width, ui.WindowW), Math.Max(MinimumSize.Height, ui.WindowH));
            Screen screen = Screen.AllScreens.FirstOrDefault(sc => sc.WorkingArea.IntersectsWith(saved));
            if (ui.WindowX >= 0 && screen != null)
            {
                Rectangle area = screen.WorkingArea;
                int w = Math.Min(saved.Width, area.Width), h = Math.Min(saved.Height, area.Height);
                int x = Math.Max(area.Left, Math.Min(saved.X, area.Right - w)), y = Math.Max(area.Top, Math.Min(saved.Y, area.Bottom - h));
                Bounds = new Rectangle(x, y, w, h);
            }
            else
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Size = new Size(Math.Min(1440, area.Width), Math.Min(920, area.Height));
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            }
            if (ui.Maximized) WindowState = FormWindowState.Maximized;
        }
        private void RememberWindow()
        {
            UiSettings ui = _config.Ui; ui.Maximized = WindowState == FormWindowState.Maximized;
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            ui.WindowX = b.X; ui.WindowY = b.Y; ui.WindowW = b.Width; ui.WindowH = b.Height;
        }
        private void Backup()
        {
            if (!Save()) return;
            using (var picker = new FolderBrowserDialog { Description = "Choose the parent folder for a new console-settings backup" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string folder = Path.Combine(picker.SelectedPath, "Dispatch_Console_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(folder);
                    File.Copy(_path, Path.Combine(folder, "console.config.xml"));
                    foreach (RadioProfile radio in _config.Radios) { string file = RadioResources.AudioFile(_data, radio.Id); if (File.Exists(file)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file))); }
                    File.WriteAllText(Path.Combine(folder, "RESTORE.txt"), "To restore: Console > Import settings... and pick console.config.xml in this folder. The console restarts with these settings (the receive-audio .settings files beside it come along). Check Receive only (Settings > Operating mode) before going Live.");
                    Log("Backup saved: " + folder); MessageBox.Show(this, folder, "Backup complete");
                }
                catch (Exception ex) { Log("Backup failed: " + ex.Message); }
            }
        }
        // A settings file that cannot be read must never stop the console starting: the previous copy (.bak) is used, else
        // first-run defaults. The unreadable file is kept beside it for inspection.
        private DispatchConfig LoadSettings(string seedDir)
        {
            try { return DispatchConfig.Load(_path, seedDir); }
            catch (Exception ex)
            {
                string kept = _path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                try { File.Copy(_path, kept, true); } catch (Exception) { kept = ""; }
                DispatchConfig recovered = null;
                try { if (File.Exists(_path + ".bak")) recovered = DispatchConfig.Load(_path + ".bak", seedDir); } catch (Exception) { recovered = null; }
                string note = "The settings file could not be read (" + ex.Message + "). " + (recovered != null ? "The previous copy (console.config.xml.bak) was loaded." : "The console started with default settings; use Console > Import settings to bring back a backup.")
                    + (kept.Length > 0 ? " The unreadable file was kept as " + Path.GetFileName(kept) + "." : "");
                if (recovered == null) recovered = DispatchConfig.Fresh(seedDir);
                recovered.SeedNote = note + " " + (recovered.SeedNote ?? "");
                return recovered;
            }
        }
        // Console > Import settings...: a console.config.xml from a backup or an older version folder replaces the current
        // settings (kept as console.config.xml.before-import), then the console restarts with them.
        private bool _skipSaveOnClose;
        private void ImportSettings()
        {
            using (var picker = new OpenFileDialog { Title = "Import console settings", Filter = "Console settings (console.config.xml)|console.config.xml|XML files (*.xml)|*.xml", CheckFileExists = true })
            {
                List<string> previous = AppPaths.PreviousConfigs(_data);
                if (previous.Count > 0) picker.InitialDirectory = Path.GetDirectoryName(previous[0]);
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (String.Equals(Path.GetFullPath(picker.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "That is the settings file the console is already using.", "Import settings"); return; }
                try { DispatchConfig test = DispatchConfig.Load(picker.FileName, _base); test.Validate(); }
                catch (Exception ex) { MessageBox.Show(this, "That file cannot be used: " + ex.Message, "Import settings"); return; }
                if (MessageBox.Show(this, "Replace the current settings with:\n" + picker.FileName + "\n\nThe current settings are kept as console.config.xml.before-import. The console restarts.", "Import settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                StopAll("Importing settings");
                try { Log("Imported " + AppPaths.CopySettings(picker.FileName, _data) + "."); }
                catch (Exception ex) { MessageBox.Show(this, "Import failed: " + ex.Message, "Import settings"); return; }
                _skipSaveOnClose = true; Application.Restart();
            }
        }
        private void OpenSettingsFolder()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + _data + "\""); } catch (Exception ex) { Log("Cannot open the settings folder: " + ex.Message); }
        }
        private void SaveText(string text, string name)
        {
            using (var picker = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = name })
                if (picker.ShowDialog(this) == DialogResult.OK) try { File.WriteAllText(picker.FileName, text, Encoding.UTF8); Log("Saved " + picker.FileName); } catch (Exception ex) { Log("Save failed: " + ex.Message); }
        }
        private void ExportLog() { SaveText(String.Join(Environment.NewLine, _history.ToArray()), "Dispatch_Console_Log_" + DateTime.Now.ToString("yyyyMMdd") + ".txt"); }
        private void ListPorts()
        {
            try { string[] ports = SerialPort.GetPortNames(); Trace("DETECTED PORTS: " + (ports.Length == 0 ? "none" : String.Join(", ", ports))); Log("COM ports: " + (ports.Length == 0 ? "none" : String.Join(", ", ports))); }
            catch (Exception ex) { Trace("Serial enumeration failed: " + ex.Message); }
        }
        private void AddRadio()
        {
            using (var dialog = new LabelDialog("NEW RADIO RESOURCE", "Additional radio"))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || String.IsNullOrWhiteSpace(dialog.ResultText)) return;
                if (_config.Radios.Count >= 32) { Log("Up to 32 radio resources."); return; }
                RadioProfile radio = DispatchConfig.NewRadio(dialog.ResultText.Trim());
                _config.Radios.Add(radio); _states[radio.Id] = new RadioState(); _selected = radio; Save(); RebuildCards(); RefreshSelected(); OpenLinksForMode();
                Log("Added " + radio.Name + ". Configure its audio devices and RIB port in Settings > Console settings.");
            }
        }
        private void EditConfig(int radioIndex)
        {
            StopAll("Settings opened");
            using (var editor = new ConfigEditor(_config, radioIndex))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                try { editor.Edited.Save(_path); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Settings not saved"); return; }
                CloseLinks(); CloseAudio(); string id = _selected.Id; _config = editor.Edited;
                _recorder.Settings = _config.Recorder;
                var keep = new Dictionary<string, RadioState>(_states); _states.Clear();
                foreach (RadioProfile radio in _config.Radios) { RadioState s; _states[radio.Id] = keep.TryGetValue(radio.Id, out s) ? s : new RadioState(); }
                _selected = _config.Radios.FirstOrDefault(r => r.Id == id) ?? _config.Radios[0]; _queue.Clear();
                BuildUi(); OpenLinksForMode(); Log("Configuration applied."); ReportHotkeyProblems();
            }
        }
        private void Capabilities()
        {
            ShowText("Capabilities and limits",
                "WHAT THE CONSOLE DOES (" + VersionText + ")\r\n" +
                "- Tone remote transmit: high-level guard 2175 Hz (120 ms) + function tone (40 ms) + continuous low-level guard tone 30 dB down under the voice, as in the XTL5000 consolette manual (Fig. 3-4). Mic audio is notched at the guard frequency and limited to the guard-tone peak.\r\n" +
                "- Channel/mode select, monitor (2050 Hz), secure coded/clear (1150/1050 Hz) and user function tones A/B/C over the wireline.\r\n" +
                "- Alerts, two-tone/Quick Call paging and DTMF transmitted as keyed tone-remote audio, with a sequential paging queue.\r\n" +
                "- SB9600 over a RIB: frame CRC, BUSY handshake with echo verification, decoded radio TX (RADKEY/SETBUT), squelch/RX (SQLDET), PL detect, active mode, head button traffic, SBEP display text and indicator LEDs. Virtual W-series head buttons (press/release/toggle), mode up/down, keypad and softkeys. PTT via the SB9600 PTT button as an alternative.\r\n" +
                "- Receive audio monitoring and recording (RX and TX, labelled by channel, kept KeepDays), instant recall.\r\n" +
                "- Radio status sync (SCAN pressed twice), RIB port reconnect, footswitch PTT (keyboard, game-controller or COM-line pedal).\r\n" +
                "- Bus monitor with LEARN (Settings > Engineering): capture what your W7 head sends and bind it to console keys.\r\n\r\n" +
                "CHECKED ON THE BENCH\r\n" +
                "- SB9600 control and readback on the XTL5000 with the W7 map (zone, mode, HOME, MON, SCAN, NUIS DEL), TRC transmit, receive audio.\r\n" +
                "- Line levels: set Windows output so the high-level guard tone reads 0 dBm (775 mVrms into 600 ohms) at the consolette, using Audio > Levels / calibration.\r\n\r\n" +
                "NOT IMPLEMENTED\r\n" +
                "- MDC1200 caller-ID decoding (planned), Smart Modem Protocol (SMP) console link, encryption keys, cross-patch, multi-position network sharing.");
        }
        private void ShowText(string title, string text)
        {
            using (var form = new TextViewer(title, text)) { Brand.Apply(form); form.ShowDialog(this); }
        }
        private void ShowAbout() { using (var about = new AboutForm()) about.ShowDialog(this); }
        internal bool SplashWanted { get { return _config.Ui.ShowSplash; } }
        private void ShowHardwareGuide() { ShowDocument("HARDWARE_SETUP.md", "Hardware setup guide"); }
        private void ShowOperatorCard() { ShowDocument("OPERATOR_CARD.md", "Operator card"); }
        // Documents ship in the version folder; the program runs from its build subfolder, so look in both.
        private void ShowDocument(string name, string title)
        {
            string file = Path.Combine(_base, name);
            if (!File.Exists(file)) file = Path.Combine(Path.GetDirectoryName(_base.TrimEnd('\\', '/')) ?? _base, name);
            ShowText(title, File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n") : name + " was not found beside the program.");
        }
    }
}
