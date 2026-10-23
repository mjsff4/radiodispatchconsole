using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    public sealed partial class ConsoleWindow
    {
        private readonly Dictionary<string, Form> _tools = new Dictionary<string, Form>();
        private Label _w7Display;
        private Control _trcSurface, _w7Surface, _alertSurface;
        private readonly List<Button> _w7TopKeys = new List<Button>(), _w7Physical = new List<Button>();
        private Control _menuAnchor;
        private readonly List<ToolStripMenuItem> _modeItems = new List<ToolStripMenuItem>();
        private void RefreshModeMenu() { foreach (ToolStripMenuItem item in _modeItems) item.Checked = item.Tag is OpMode && (OpMode)item.Tag == _opMode; }
        private void DisposeTools()
        {
            foreach (Form f in _tools.Values) f.Dispose();
            _tools.Clear(); _w7TopKeys.Clear(); _w7Physical.Clear();
        }
        // Tool windows forward every keyboard shortcut to the console, so PTT, STOP ALL and quick keys still work while
        // the mixer, queue or recorder window has focus. Losing focus while a PTT key is held releases TX.
        private sealed class ToolForm : Form
        {
            public ConsoleWindow Owner2;
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                bool typing = ConsoleWindow.IsPlainKey(keyData) && ConsoleWindow.FocusedIsText(this);
                if (Owner2 != null && !typing && (msg.Msg == 0x100 || msg.Msg == 0x104) && Owner2.HandleHotkey(keyData, ConsoleWindow.IsRepeat(ref msg))) return true;
                return base.ProcessCmdKey(ref msg, keyData);
            }
        }
        internal void AttachHotkeys(Form f)
        {
            f.KeyPreview = true;
            f.KeyUp += delegate(object sender, KeyEventArgs e) { if (HandleHotkeyUp(e.KeyCode)) e.SuppressKeyPress = true; };
            f.Deactivate += delegate { if (_heldPttKey != Keys.None && IsHoldPtt()) { _heldPttKey = Keys.None; StopTx("Tool window lost focus"); } };
        }
        private void RegisterTool(string key, string title, Control content) { RegisterTool(key, title, content, new Size(1040, 700)); }
        private void RegisterTool(string key, string title, Control content, Size size)
        {
            var f = new ToolForm { Owner2 = this, Text = Brand.Product + " - " + title, Size = FitToScreen(size), MinimumSize = new Size(Math.Min(600, size.Width), 400), StartPosition = FormStartPosition.CenterParent, BackColor = _t.Panel, ForeColor = _t.Text, Font = _t.Base, ShowInTaskbar = false };
            Brand.Apply(f); content.Dock = DockStyle.Fill; f.Controls.Add(content); AttachHotkeys(f);
            f.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!_closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; f.Hide(); } };
            _tools[key] = f;
        }
        private void ShowTool(string key)
        {
            key = (key ?? "").ToUpperInvariant();
            if (key == "CONTROL" || key == "CHANNELS") { Reveal(_channelKeys); return; }
            if (key == "ALERTS") { Reveal(_alertSurface); return; }
            if (key == "HEAD" && !_tools.ContainsKey("HEAD")) { Reveal(_w7Surface); return; }
            Form f; if (_tools.TryGetValue(key, out f)) { if (!f.Visible) f.Show(this); f.BringToFront(); }
        }
        // Brings a main-screen panel forward: selects its tab in the Compact layout, then focuses it.
        private static void Reveal(Control c)
        {
            if (c == null) return;
            for (Control p = c; p != null; p = p.Parent)
            {
                var page = p as TabPage; var tabs = page != null ? page.Parent as TabControl : null;
                if (tabs != null) tabs.SelectedTab = page;
            }
            c.Focus();
        }
        private sealed class OperationsMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly Color _background;
            public OperationsMenuRenderer(Color background) { _background = background; }
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                if (e.ToolStrip is MenuStrip) { using (var brush = new SolidBrush(_background)) e.Graphics.FillRectangle(brush, e.AffectedBounds); }
                else base.OnRenderToolStripBackground(e);
            }
        }
        private Control BuildMenuBar()
        {
            var bar = new MenuStrip { Dock = DockStyle.Fill, AutoSize = false, BackColor = _t.Panel, ForeColor = _t.Text, Font = _t.Bold, GripStyle = ToolStripGripStyle.Hidden };
            bar.Renderer = new OperationsMenuRenderer(_t.Panel);
            MainMenuStrip = bar;
            var console = new ToolStripMenuItem("Console"); bar.Items.Add(console);
            console.DropDownItems.Add("Save settings", null, delegate { Save(); });
            console.DropDownItems.Add("Backup settings...", null, delegate { Backup(); });
            console.DropDownItems.Add("Import settings...", null, delegate { ImportSettings(); });
            console.DropDownItems.Add("Open settings folder", null, delegate { OpenSettingsFolder(); });
            console.DropDownItems.Add("Add radio...", null, delegate { AddRadio(); });
            console.DropDownItems.Add("Add quick key...", null, delegate { EditQuickKey(-1); });
            console.DropDownItems.Add("Import CPS codeplug (selected radio)...", null, delegate { ImportCodeplug(); });
            console.DropDownItems.Add("-");
            console.DropDownItems.Add("Operator card...", null, delegate { ShowOperatorCard(); });
            console.DropDownItems.Add("Keyboard shortcuts...", null, delegate { ShowHotkeys(); });
            console.DropDownItems.Add("About...", null, delegate { ShowAbout(); });
            var settings = new ToolStripMenuItem("Settings"); bar.Items.Add(settings);
            settings.DropDownItems.Add("Console settings...", null, delegate { EditConfig(-1); });
            settings.DropDownItems.Add("Appearance...", null, delegate { EditAppearance(); });
            settings.DropDownItems.Add("Layouts and themes...", null, delegate { ShowLayoutMenu(_menuAnchor); });
            settings.DropDownItems.Add("Show / hide panels...", null, delegate { ShowViewMenu(_menuAnchor); });
            settings.DropDownItems.Add("-");
            // Operating mode: Live is normal dispatch; Receive only and Test (simulated radio) are for setup and bench work.
            var mode = new ToolStripMenuItem("Operating mode"); settings.DropDownItems.Add(mode); _modeItems.Clear();
            foreach (OpMode m in new OpMode[] { OpMode.Live, OpMode.LiveReceive, OpMode.Simulation })
            {
                OpMode mm = m;
                var item = new ToolStripMenuItem(m == OpMode.Live ? "Live (normal dispatch)" : m == OpMode.LiveReceive ? "Receive only (nothing is transmitted)" : "Test mode (simulated radio, nothing is sent)");
                item.Click += delegate { if (_opMode != mm) SetMode(mm); }; item.Tag = mm; mode.DropDownItems.Add(item); _modeItems.Add(item);
            }
            // Engineering: setup, learning and test tools, kept off the operating screen.
            var eng = new ToolStripMenuItem("Engineering"); settings.DropDownItems.Add(eng);
            eng.DropDownItems.Add("SB9600 monitor / LEARN...", null, delegate { OpenBusMonitor(); });
            eng.DropDownItems.Add("W7 mappings, display names and simulation events...", null, delegate { ShowTool("HEADTOOLS"); });
            eng.DropDownItems.Add("Diagnostics (trace, capture decoder, notes)...", null, delegate { ShowTool("DIAGNOSTICS"); });
            eng.DropDownItems.Add("Tone test files and WAV export...", null, delegate { ShowTool("TESTFILES"); });
            eng.DropDownItems.Add("Restart radio (full resync over SB9600)...", null, delegate { ResyncRadio(); });
            eng.DropDownItems.Add("-");
            eng.DropDownItems.Add("Hardware setup guide...", null, delegate { ShowHardwareGuide(); });
            eng.DropDownItems.Add("Action reference (quick keys / softkeys)...", null, delegate { ShowActionReference(); });
            eng.DropDownItems.Add("Capabilities and limits...", null, delegate { Capabilities(); });
            var audio = new ToolStripMenuItem("Audio"); bar.Items.Add(audio);
            audio.DropDownItems.Add("Mixer", null, delegate { ShowTool("MIXER"); });
            audio.DropDownItems.Add("Receive audio", null, delegate { OpenAudio(); });
            audio.DropDownItems.Add("TX routing...", null, delegate { ConfigureRouting(); });
            audio.DropDownItems.Add("Levels / calibration...", null, delegate { OpenCalibration(); });
            audio.DropDownItems.Add("Recorder / instant recall", null, delegate { ShowTool("RECORDER"); });
            var tools = new ToolStripMenuItem("Tools"); bar.Items.Add(tools);
            tools.DropDownItems.Add("W7 control head", null, delegate { ShowTool("HEAD"); });
            tools.DropDownItems.Add("Paging queue", null, delegate { ShowTool("QUEUE"); });
            tools.DropDownItems.Add("Sync radio status (scan light, display)", null, delegate { SyncRadioStatus(false); });
            tools.DropDownItems.Add("DTMF...", null, delegate { ShowTool("DTMF"); });
            tools.DropDownItems.Add("Export journal...", null, delegate { ExportLog(); });
            RefreshModeMenu();
            // Pop-up menus open under the left of the menu bar.
            var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = _t.Panel };
            _menuAnchor = new Panel { Width = 1, Height = 1, Location = new Point(0, 0), BackColor = _t.Panel };
            host.Controls.Add(bar); host.Controls.Add(_menuAnchor); _menuAnchor.BringToFront();
            return host;
        }
        // Main working area. One button per control: radio functions (MON, SCAN, NUIS DEL ...) are quick keys on the bar at
        // the bottom, so they are not repeated here. Full layout: zone / channel keys | alerts + paging side by side.
        // Compact layout (small screens): the same two panels as tabs. The W7 head opens in its own window (Tools > W7 control
        // head, or click the radio display) unless Settings > Appearance > ShowHeadPanel puts it on the main screen (Full only).
        // The mixer, recorder, paging queue, tone test files, W7 mapping tools and diagnostics open as tool windows.
        private Control BuildOperations()
        {
            // Zone / channel keys (built by BuildControlTab).
            var channels = (TableLayoutPanel)BuildControlTab();
            channels.Dock = DockStyle.Fill; channels.Margin = new Padding(0, 0, _t.S(5), 0); channels.Name = "ChannelControls";
            _trcSurface = channels;
            // Alert keys, presets, DTMF; test files and the paging queue become tool windows.
            var alerts = (TableLayoutPanel)BuildAlerts();
            Control alertPanel = alerts.GetControlFromPosition(0, 0), queue = alerts.GetControlFromPosition(1, 0);
            alerts.Controls.Remove(alertPanel); alerts.Controls.Remove(queue); alerts.Dispose();
            _alertButtons.AutoScroll = true;
            var alertFlow = (FlowLayoutPanel)alertPanel;
            var testFiles = Stack();
            while (alertFlow.Controls.Count > 6) { Control item = alertFlow.Controls[6]; alertFlow.Controls.Remove(item); testFiles.Controls.Add(item); }
            FlowLayoutPanel exports = Row(560);
            exports.Controls.Add(_t.MakeButton("EXPORT SELECTED ALERT WAV", delegate { ExportAlert(); }, 230)); exports.Controls.Add(_t.MakeButton("EXPORT DTMF WAV", delegate { Dtmf(true); }, 160));
            testFiles.Controls.Add(Heading("ALERT / DTMF WAV EXPORT (uses the alert and DTMF digits chosen on the main screen)", 560)); testFiles.Controls.Add(exports);
            RegisterTool("TESTFILES", "Tone test files and WAV export", testFiles);
            // DTMF is rarely used: it lives in its own window (Tools > DTMF), off the main screen.
            var dtmfPage = Stack();
            while (alertFlow.Controls.Count > 4) { Control item = alertFlow.Controls[4]; alertFlow.Controls.Remove(item); dtmfPage.Controls.Add(item); }
            RegisterTool("DTMF", "DTMF", dtmfPage, new Size(620, 260));
            RegisterTool("QUEUE", "Paging queue - ordered alerts for selected radios and channels", queue, new Size(760, 560));
            alertPanel.Dock = DockStyle.Fill; alertPanel.Margin = new Padding(0, 0, _t.S(5), 0); alertPanel.Name = "AlertControls";
            alertFlow.Controls.Add(_t.MakeButton("PAGING QUEUE...", delegate { ShowTool("QUEUE"); }, 160));
            _alertSurface = alertPanel;
            // The W7 control head.
            var headTools = (FlowLayoutPanel)BuildHead();
            headTools.Controls.Remove(headTools.Controls[1]);
            headTools.Controls.Remove(_softButtons); headTools.Controls.Remove(_headMirror);
            RegisterTool("HEADTOOLS", "W7 mappings, display names and simulation events", headTools);
            _w7Surface = BuildW7();
            bool headOnMain = _config.Ui.ShowHeadPanel && !_compact;
            if (!headOnMain) RegisterTool("HEAD", "W7 control head (SB9600)", _w7Surface, new Size(_t.S(660), _t.S(520)));
            RegisterTool("MIXER", "Audio mixer", BuildMixer(), new Size(820, 640)); RegisterTool("RECORDER", "Recorder and instant recall", BuildRecorder()); RegisterTool("DIAGNOSTICS", "Diagnostics", BuildDiagnostics());
            if (_compact)
            {
                // Dock = Fill: without it the tab control keeps the Windows default 200 x 100 and the channel keys get ~56 px (v0.8.4 bug).
                var tabs = new ThemedTabs(_t) { Name = "CompactOperations", Dock = DockStyle.Fill, Margin = new Padding(0) };
                AddPage(tabs, "CHANNELS", channels);
                AddPage(tabs, "ALERTS", alertPanel);
                if (_cardsInTab && _cards != null) AddPage(tabs, "RADIOS (" + _config.Radios.Count + ")", _cards);
                return tabs;
            }
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = headOnMain ? 3 : 2, RowCount = 1, Margin = new Padding(0), Name = "UnifiedOperations" };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _t.S(580)));
            if (headOnMain) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(channels, 0, 0); grid.Controls.Add(alertPanel, 1, 0);
            if (headOnMain) grid.Controls.Add(_w7Surface, 2, 0);
            // Scrolls instead of squeezing when the window is made small.
            int minW = _t.S(headOnMain ? 1300 : 900), minH = _t.S(300);
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
            grid.Dock = DockStyle.None; grid.Location = Point.Empty; viewport.Controls.Add(grid);
            viewport.Resize += delegate
            {
                int w = viewport.ClientSize.Width, h = viewport.ClientSize.Height;
                bool needH = w < minW, needV = h < minH;
                grid.Size = new Size(Math.Max(minW, w - (needV ? SystemInformation.VerticalScrollBarWidth : 0)), Math.Max(minH, h - (needH ? SystemInformation.HorizontalScrollBarHeight : 0)));
            };
            grid.Size = new Size(minW, minH);
            return viewport;
        }
        private void ImportCodeplug()
        {
            StopAll("Codeplug import");
            using (var dialog = new CpsImportDialog(_selected))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                dialog.ApplyTo(_selected); Save(); BuildUi(); OpenLinksForMode();
                Log(_selected.Name + ": " + dialog.Summary.Replace("\n", " "));
                MessageBox.Show(this, dialog.Summary, "CPS import");
            }
        }
        private void ShowHotkeys()
        {
            using (var viewer = new TextViewer("Keyboard shortcuts", HotkeyReference())) viewer.ShowDialog(this);
        }
        private Button PhysicalKey(string legend, string binding)
        {
            Button b = _t.MakeButton(legend, delegate { HeadKey(binding); }, 70);
            b.Dock = DockStyle.Fill; b.Margin = new Padding(2); b.Name = "W7_" + binding; b.Font = _t.Small; b.Padding = new Padding(0); b.Tag = binding;
            _w7Physical.Add(b);
            _tips.SetToolTip(b, "Head binding: " + binding + ". W-series codes must be verified or learned on your W7. Secondary keypad functions depend on the radio codeplug.");
            return b;
        }
        private void RefreshW7Keys()
        {
            if (_selected == null) return;
            // Keys the codeplug does not use are greyed out (Settings > radio tab > SB9600 / RIB > W7 faceplate > UnusedKeys).
            var unused = new HashSet<string>((_selected.Bus.UnusedKeys ?? "").Split(new char[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            foreach (Button k in _w7Physical) { string bind = k.Tag as string ?? ""; bool on = bind != "POWER" && !unused.Contains(bind); if (k.Enabled != on) k.Enabled = on; }
            if (_w7TopKeys.Count != 5) return;
            BusSettings b = _selected.Bus;
            string[] labels = { b.W7Top1Label, b.W7Top2Label, b.W7Top3Label, b.W7Top4Label, "SEL" };
            string[] bindings = { b.W7Top1Binding, b.W7Top2Binding, b.W7Top3Binding, b.W7Top4Binding, "SEL" };
            string[] fallbackLabels = { "EMER", "PHON", "SCAN", "CALL", "SEL" };
            string[] fallbackBindings = { "TOP1", "TOP2", "TOP3", "TOP4", "SEL" };
            for (int i = 0; i < 5; i++)
            {
                _w7TopKeys[i].Text = String.IsNullOrWhiteSpace(labels[i]) ? fallbackLabels[i] : labels[i]; _w7TopKeys[i].Tag = String.IsNullOrWhiteSpace(bindings[i]) ? fallbackBindings[i] : bindings[i];
                bool on = !unused.Contains((string)_w7TopKeys[i].Tag); if (_w7TopKeys[i].Enabled != on) _w7TopKeys[i].Enabled = on;
                _tips.SetToolTip(_w7TopKeys[i], on ? "Binding: " + _w7TopKeys[i].Tag + "." : "Not used on this codeplug.");
            }
        }
        private Control BuildW7()
        {
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(6), BackColor = _t.Panel, Name = "W7Controls" };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(28))); outer.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(220)));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(46))); outer.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(26)));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(48)));
            Label title = Heading("SB9600  /  W7 CONTROL HEAD", 600); title.Dock = DockStyle.Fill; outer.Controls.Add(title, 0, 0);
            var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.FromArgb(27, 30, 33), Padding = new Padding(5) };
            head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27)); head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = new Padding(0) };
            for (int i = 0; i < 2; i++) left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 4; i++) left.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            var power = PhysicalKey("POWER", "POWER"); power.Enabled = false; _tips.SetToolTip(power, "Power is not mapped. The physical radio power switch remains in use."); left.Controls.Add(power, 0, 0); left.SetColumnSpan(power, 2);
            left.Controls.Add(PhysicalKey("MODE\n-", "MODE DN"), 0, 1); left.Controls.Add(PhysicalKey("MODE\n+", "MODE UP"), 1, 1);
            left.Controls.Add(PhysicalKey("VOL\n-", "VOL DN"), 0, 2); left.Controls.Add(PhysicalKey("VOL\n+", "VOL UP"), 1, 2);
            left.Controls.Add(PhysicalKey("DIM", "DIM"), 0, 3); left.Controls.Add(PhysicalKey("HOME", "HOME"), 1, 3); head.Controls.Add(left, 0, 0);
            var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(4) };
            center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0) };
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            string[] labels = { "EMER", "PHON", "SCAN", "CALL", "SEL" }; string[] bindings = { "TOP1", "TOP2", "TOP3", "TOP4", "SEL" };
            for (int i = 0; i < 5; i++) { top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20)); Button b = _t.MakeButton(labels[i], null, 70); b.Dock = DockStyle.Fill; b.Margin = new Padding(1); b.Font = _t.Small; b.Padding = new Padding(0); b.Name = "W7Top" + (i + 1); b.Tag = bindings[i]; b.Click += delegate { HeadKey((string)b.Tag); }; _w7TopKeys.Add(b); top.Controls.Add(b, i, 0); }
            center.Controls.Add(top, 0, 0);
            _w7Display = new Label { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 35, 26), ForeColor = Color.FromArgb(180, 235, 181), Font = _t.Mono, TextAlign = ContentAlignment.MiddleCenter, Text = "WAITING FOR DISPLAY", Margin = new Padding(4), AutoEllipsis = true };
            center.Controls.Add(_w7Display, 0, 1); head.Controls.Add(center, 1, 0);
            var pad = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Margin = new Padding(0), Name = "W7Keypad" };
            for (int i = 0; i < 3; i++) pad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3)); for (int i = 0; i < 4; i++) pad.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            string[] keys = { "1\nZone+", "2\nPge", "3\nZone-", "4\nSts", "5\nRpg", "6\nMsg", "7\nH/L", "8\nMon", "9\nDir", "*\nRcl", "0", "#\nNuis Del" };
            for (int i = 0; i < keys.Length; i++) pad.Controls.Add(PhysicalKey(keys[i], "123456789*0#"[i].ToString()), i % 3, i / 3);
            head.Controls.Add(pad, 2, 0); outer.Controls.Add(head, 0, 1);
            _headMirror.Dock = DockStyle.Fill; _headMirror.AutoEllipsis = true; outer.Controls.Add(_headMirror, 0, 2);
            var softTitle = Heading("CUSTOM RADIO ACTIONS", 600); softTitle.Dock = DockStyle.Fill; outer.Controls.Add(softTitle, 0, 3);
            _softButtons.Dock = DockStyle.Fill; _softButtons.AutoScroll = true; outer.Controls.Add(_softButtons, 0, 4);
            Label note = Note("Top-key legends are a starting template: change them in Settings > Console settings > radio tab > SB9600 / RIB > W7 faceplate, and confirm every code with LEARN. 7 H/L = horn/lights. POWER is not mapped.", 600, 48); note.AutoEllipsis = true; note.Dock = DockStyle.Fill; outer.Controls.Add(note, 0, 5);
            return outer;
        }
    }
}
