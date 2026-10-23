using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    internal static class DialogKit
    {
        public static DataGridView Grid<T>(List<T> list)
        {
            var grid = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = true, DataSource = new BindingList<T>(list), AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersWidth = 28 };
            grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; MessageBox.Show(grid, e.Exception != null ? e.Exception.Message : "Invalid value", "Check the value"); e.Cancel = true; };
            grid.DataBindingComplete += delegate { EnumCombos(grid); };
            return grid;
        }
        // Replace auto-generated text columns for enum properties with drop-downs.
        private static void EnumCombos(DataGridView grid)
        {
            foreach (DataGridViewColumn col in grid.Columns.Cast<DataGridViewColumn>().ToArray())
            {
                if (col is DataGridViewComboBoxColumn || col.ValueType == null || !col.ValueType.IsEnum) continue;
                var combo = new DataGridViewComboBoxColumn { DataPropertyName = col.DataPropertyName, HeaderText = col.HeaderText, DataSource = Enum.GetValues(col.ValueType), ValueType = col.ValueType, FlatStyle = FlatStyle.Flat };
                int index = col.Index; grid.Columns.Remove(col); grid.Columns.Insert(index, combo);
            }
        }
        public static Button Button(string text, int width, EventHandler click) { var b = new Button { Text = text, Width = width, Height = 30, Margin = new Padding(4) }; if (click != null) b.Click += click; return b; }
        public static FlowLayoutPanel Bar() { return new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4), WrapContents = false }; }
    }

    // ---------------- full settings editor ----------------
    internal sealed class ConfigEditor : Form
    {
        public DispatchConfig Edited;
        private readonly List<DataGridView> _grids = new List<DataGridView>();
        public ConfigEditor(DispatchConfig current, int radioIndex)
        {
            Edited = current.Copy(); Text = "Console settings"; Size = ConsoleWindow.FitToScreen(new Size(1060, 800));
            StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(760, 560);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            AddGridPage(tabs, "Console", Edited, "Console name, operator, PTT timeout, live-TX confirmation and the operator speaker used for alert audition and instant recall.");
            AddGridPage(tabs, "Hotkeys", Edited.Hotkeys, "Key names such as F12, Shift+F12, Ctrl+1, PageUp, Escape. PTT and group PTT are hold-to-talk.");
            AddGridPage(tabs, "Recorder", Edited.Recorder, "Instant recall keeps recent calls in memory. Disk logging writes one WAV per call and a CSV index per day.");
            AddGridPage(tabs, "Footswitch", Edited.Footswitch, "Footswitch PTT that works whichever window has focus. Most USB pedals type a key: choose KeyboardKey and set Key to what the pedal sends (program it to F13). The FOOT light on the main screen shows the pedal.");
            AddGridPage(tabs, "Appearance", Edited.Ui, "Theme, scale, card layout and panel visibility. Settings > Appearance on the main screen also edits these.");
            int firstRadioPage = tabs.TabPages.Count;
            foreach (RadioProfile radio in Edited.Radios) tabs.TabPages.Add(RadioPage(radio));
            tabs.TabPages.Add(AlertsPage());
            var quick = new TabPage("Quick keys");
            DataGridView qg = DialogKit.Grid(Edited.QuickKeys); _grids.Add(qg); quick.Controls.Add(qg);
            quick.Controls.Add(Hint("Label, action (see Settings > Engineering > Action reference), colour #RRGGBB and optional hotkey. Right-click a quick key on the main screen to edit it in place."));
            tabs.TabPages.Add(quick);
            var layouts = new TabPage("Saved layouts");
            DataGridView lg = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, DataSource = new BindingList<LayoutPreset>(Edited.Layouts), AllowUserToAddRows = false, AllowUserToDeleteRows = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            lg.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Layout name (select row + Delete to remove)" });
            _grids.Add(lg); layouts.Controls.Add(lg); tabs.TabPages.Add(layouts);
            if (radioIndex >= 0 && firstRadioPage + radioIndex < tabs.TabPages.Count) tabs.SelectedIndex = firstRadioPage + radioIndex;
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var save = new Button { Text = "Apply", Width = 120, Height = 30 }; var cancel = new Button { Text = "Cancel", Width = 120, Height = 30, DialogResult = DialogResult.Cancel };
            save.Click += delegate
            {
                try
                {
                    foreach (DataGridView g in _grids) g.EndEdit();
                    ValidateChildren();
                    Edited.Radios.RemoveAll(r => r == null); foreach (RadioProfile r in Edited.Radios) { r.Channels.RemoveAll(c => c == null); r.Softkeys.RemoveAll(k => k == null); r.Bus.HeadButtons.RemoveAll(b => b == null); r.Bus.Rules.RemoveAll(x => x == null); r.Bus.DisplayLookups.RemoveAll(x => x == null || String.IsNullOrWhiteSpace(x.Match)); }
                    Edited.QuickKeys.RemoveAll(k => k == null || String.IsNullOrWhiteSpace(k.Label));
                    Edited.Normalize(); Edited.Validate(); DialogResult = DialogResult.OK; Close();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check configuration"); }
            };
            footer.Controls.Add(save); footer.Controls.Add(cancel);
            Controls.Add(tabs); Controls.Add(footer); CancelButton = cancel;
        }
        private static Label Hint(string text) { return new Label { Text = text, Dock = DockStyle.Top, Height = 38, Padding = new Padding(6), ForeColor = Color.FromArgb(90, 70, 0) }; }
        private static void AddGridPage(TabControl tabs, string title, object target, string hint)
        {
            var page = new TabPage(title);
            page.Controls.Add(new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = target, HelpVisible = true, ToolbarVisible = true, PropertySort = PropertySort.Categorized });
            page.Controls.Add(Hint(hint)); tabs.TabPages.Add(page);
        }
        private TabPage RadioPage(RadioProfile radio)
        {
            var page = new TabPage(radio.Name);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            layout.Controls.Add(new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = radio, PropertySort = PropertySort.Categorized }, 0, 0);
            var sub = new TabControl { Dock = DockStyle.Fill };
            var channels = new TabPage("Channels / modes");
            DataGridView cg = DialogKit.Grid(radio.Channels); _grids.Add(cg); channels.Controls.Add(cg);
            FlowLayoutPanel cbar = DialogKit.Bar();
            cbar.Controls.Add(DialogKit.Button("Apply standard TRC tone table to modes 1-8", 300, delegate
            {
                int[] hz = { 1950, 1850, 1350, 1250, 1550, 1450, 1750, 1650 };
                for (int i = 0; i < radio.Channels.Count && i < 8; i++) radio.Channels[i].FunctionHz = hz[i];
                cg.Refresh(); MessageBox.Show(this, "Modes 1-8 now use the consolette's standard tone table (manual Table 3-1, S100-5..8 all OFF).", "Tone table");
            }));
            cbar.Controls.Add(DialogKit.Button("Import from CPS CSV export...", 220, delegate
            {
                using (var dialog = new CpsImportDialog(radio))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    cg.DataSource = null; dialog.ApplyTo(radio); cg.DataSource = new BindingList<ChannelProfile>(radio.Channels);
                    MessageBox.Show(this, dialog.Summary, "CPS import");
                }
            }));
            cbar.Controls.Add(new Label { Text = "Standard table: M1 1950  M2 1850  M3 1350  M4 1250  M5 1550  M6 1450  M7 1750  M8 1650 Hz", AutoSize = true, Padding = new Padding(6, 8, 0, 0) });
            channels.Controls.Add(cbar); sub.TabPages.Add(channels);
            var tone = new TabPage("Tone remote"); tone.Controls.Add(new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = radio.Tone, PropertySort = PropertySort.Categorized }); sub.TabPages.Add(tone);
            var bus = new TabPage("SB9600 / RIB"); bus.Controls.Add(new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = radio.Bus, PropertySort = PropertySort.Categorized }); sub.TabPages.Add(bus);
            var heads = new TabPage("Head buttons");
            DataGridView hg = DialogKit.Grid(radio.Bus.HeadButtons); _grids.Add(hg); heads.Controls.Add(hg);
            FlowLayoutPanel hbar = DialogKit.Bar();
            hbar.Controls.Add(DialogKit.Button("Reset to W-series (W9/W7) map", 230, delegate
            {
                if (MessageBox.Show(this, "Replace this radio's head-button list with the W-series defaults?", "Head buttons", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                hg.DataSource = null; radio.Bus.HeadButtons.Clear(); BusSettings.AddWSeriesButtons(radio.Bus.HeadButtons); hg.DataSource = new BindingList<HeadButton>(radio.Bus.HeadButtons);
            }));
            hbar.Controls.Add(new Label { Text = "Names are used by head:NAME actions, softkeys and keypad. LEARN in the bus monitor fills these from your W7.", AutoSize = true, Padding = new Padding(6, 8, 0, 0) });
            heads.Controls.Add(hbar); sub.TabPages.Add(heads);
            var rules = new TabPage("Bus rules");
            DataGridView rg = DialogKit.Grid(radio.Bus.Rules); _grids.Add(rg); rules.Controls.Add(rg);
            rules.Controls.Add(Hint("Extra events from bus frames, e.g. an emergency alarm. Pattern = address param1 param2 opcode in hex, ** = any. TX, RX, monitor and mode are already decoded.")); sub.TabPages.Add(rules);
            var looks = new TabPage("Display lookups");
            DataGridView lg2 = DialogKit.Grid(radio.Bus.DisplayLookups); _grids.Add(lg2); looks.Controls.Add(lg2);
            looks.Controls.Add(Hint("Short radio display text -> full text, e.g. CITY FD -> CITY FIRE DEPT. A channel's RadioText (Channels tab) takes priority and also selects that channel. PartialWord replaces the text where it appears inside a longer display."));
            sub.TabPages.Add(looks);
            var keys = new TabPage("Softkeys");
            DataGridView kg = DialogKit.Grid(radio.Softkeys); _grids.Add(kg); keys.Controls.Add(kg);
            keys.Controls.Add(Hint("Label shown under CUSTOM RADIO ACTIONS on the W7 panel and the action it runs, e.g. head:TOP1, func:SCAN, trc:MON. Softkeys bound to head:TOP1..TOP6 light with the radio's top-row indicators."));
            sub.TabPages.Add(keys);
            layout.Controls.Add(sub, 0, 1);
            page.Controls.Add(layout);
            return page;
        }
        private TabPage AlertsPage()
        {
            var page = new TabPage("Alerts / paging");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var left = new Panel { Dock = DockStyle.Fill };
            var names = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false }; var properties = new PropertyGrid { Dock = DockStyle.Fill };
            foreach (AlertProfile alert in Edited.Alerts) names.Items.Add(alert);
            names.SelectedIndexChanged += delegate { properties.SelectedObject = names.SelectedItem; };
            properties.PropertyValueChanged += delegate { int i = names.SelectedIndex; if (i >= 0) { names.Items[i] = names.Items[i]; } };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 116 };
            actions.Controls.Add(DialogKit.Button("Add tone set", 110, delegate { var alert = AlertProfile.NewToneSet(); Edited.Alerts.Add(alert); names.Items.Add(alert); names.SelectedItem = alert; }));
            actions.Controls.Add(DialogKit.Button("Add alert", 90, delegate { var alert = new AlertProfile(); Edited.Alerts.Add(alert); names.Items.Add(alert); names.SelectedItem = alert; }));
            actions.Controls.Add(DialogKit.Button("Duplicate", 90, delegate { AlertProfile a = names.SelectedItem as AlertProfile; if (a == null) return; AlertProfile c = a.Copy(); c.Name = a.Name + " COPY"; Edited.Alerts.Add(c); names.Items.Add(c); names.SelectedItem = c; }));
            actions.Controls.Add(DialogKit.Button("Remove", 80, delegate { if (names.SelectedItem == null || Edited.Alerts.Count < 2) return; Edited.Alerts.Remove((AlertProfile)names.SelectedItem); names.Items.Remove(names.SelectedItem); }));
            actions.Controls.Add(DialogKit.Button("Up", 60, delegate { MoveAlert(names, -1); }));
            actions.Controls.Add(DialogKit.Button("Down", 60, delegate { MoveAlert(names, 1); }));
            left.Controls.Add(names); left.Controls.Add(actions);
            layout.Controls.Add(left, 0, 0); layout.Controls.Add(properties, 1, 0);
            page.Controls.Add(layout);
            page.Controls.Add(Hint("Add tone set = a two-tone paging set (tone A, then tone B; Quick Call II timing). Tick ShowOnMain to give it a key on the main screen; Up / Down sets the key order. Enter your department's verified tone pairs before live use."));
            if (names.Items.Count > 0) names.SelectedIndex = 0;
            return page;
        }
        private void MoveAlert(ListBox names, int delta)
        {
            int i = names.SelectedIndex, j = i + delta; if (i < 0 || j < 0 || j >= Edited.Alerts.Count) return;
            AlertProfile a = Edited.Alerts[i]; Edited.Alerts.RemoveAt(i); Edited.Alerts.Insert(j, a);
            names.Items.RemoveAt(i); names.Items.Insert(j, a); names.SelectedIndex = j;
        }
    }

    // A text box that captures the next key or combination (including Esc, Enter, F10 and arrows) instead of letting
    // the dialog act on it. Tab still moves focus; Backspace or Delete clears.
    internal sealed class HotkeyBox : TextBox
    {
        public HotkeyBox() { ReadOnly = true; BackColor = Color.White; ShortcutsEnabled = false; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu) return true;
            if ((code == Keys.Back || code == Keys.Delete) && (keyData & Keys.Modifiers) == Keys.None) { Text = ""; return true; }
            Text = Hotkey.Format(keyData); SelectionStart = Text.Length; return true;
        }
    }

    // ---------------- CPS codeplug import ----------------
    internal sealed class CpsImportDialog : Form
    {
        private readonly TextBox _file = new TextBox(), _rxOnly = new TextBox(), _home = new TextBox();
        private readonly RadioButton _noMode = new RadioButton(), _standard = new RadioButton();
        private readonly CheckBox _busSelect = new CheckBox();
        private List<ChannelProfile> _channels; private int _homeIndex;
        public string Summary = "";
        public CpsImportDialog(RadioProfile radio)
        {
            Text = "Import channels from ASTRO 25 CPS export"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(700, 470); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
            flow.Controls.Add(new Label { Width = 660, Height = 36, Text = "CPS: File > Export to Applications (CSV). Needs the Zone Channel Assignment feature; Scan List is optional.\r\nThe radio's codeplug stays authoritative - this only teaches the console the zones, names, tones and RX-only channels." });
            var fileRow = new FlowLayoutPanel { Width = 660, Height = 36 };
            _file.Width = 520; fileRow.Controls.Add(_file);
            fileRow.Controls.Add(DialogKit.Button("Browse...", 100, delegate { using (var p = new OpenFileDialog { Filter = "CPS CSV export (*.csv)|*.csv|All files (*.*)|*.*" }) if (p.ShowDialog(this) == DialogResult.OK) _file.Text = p.FileName; }));
            flow.Controls.Add(fileRow);
            flow.Controls.Add(new Label { Width = 660, Height = 20, Text = "Receive-only personalities (comma separated, e.g. 3,6):" });
            _rxOnly.Width = 200; _rxOnly.Text = "3,6"; flow.Controls.Add(_rxOnly);
            flow.Controls.Add(new Label { Width = 660, Height = 20, Text = "Home channel as zone/channel (e.g. 1/2):" });
            _home.Width = 200; _home.Text = "1/2"; flow.Controls.Add(_home);
            flow.Controls.Add(new Label { Width = 660, Height = 20, Text = "TRC board tone table (S100-5..8):" });
            _noMode.Text = "No Mode Select (S100-6 ON) - tone remote only keys/monitors; channels change over SB9600. Recommended for 59 channels."; _noMode.Width = 660; _noMode.Height = 36; _noMode.Checked = true;
            _standard.Text = "Standard (all OFF) - Zone 1 channels 1-8 get mode-select tones. Every PTT also selects that mode!"; _standard.Width = 660; _standard.Height = 36;
            flow.Controls.Add(_noMode); flow.Controls.Add(_standard);
            _busSelect.Text = "Select channels with SB9600 MODE / ZONE head keys (confirmed by the radio display)"; _busSelect.Checked = true; _busSelect.Width = 660; flow.Controls.Add(_busSelect);
            flow.Controls.Add(new Label { Width = 660, Height = 36, ForeColor = Color.DarkRed, Text = "Full names come from channel_names.csv beside the CSV (RadioText,FullName); edit them afterwards in the Channels grid (Name column)." });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var ok = DialogKit.Button("Import", 100, null); var cancel = DialogKit.Button("Cancel", 90, null); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { if (TryLoad()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            Controls.Add(flow); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
        }
        private bool TryLoad()
        {
            try
            {
                if (!File.Exists(_file.Text)) { MessageBox.Show(this, "Choose the CPS CSV export file."); return false; }
                CpsImportResult result = CpsImport.Parse(File.ReadAllLines(_file.Text));
                if (result.Channels.Count == 0) { MessageBox.Show(this, String.Join("\n", result.Warnings.ToArray()), "Nothing imported"); return false; }
                if (result.Channels.Count > 512) { MessageBox.Show(this, "More than 512 channels."); return false; }
                var rxOnly = new HashSet<int>();
                foreach (string part in _rxOnly.Text.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)) { int n; if (Int32.TryParse(part, out n)) rxOnly.Add(n); }
                _channels = CpsImport.ToChannels(result, CpsImport.LoadNames(Path.GetDirectoryName(_file.Text)), rxOnly, _noMode.Checked);
                _homeIndex = 0; string[] hz = _home.Text.Split('/');
                int hzone, hch;
                if (hz.Length == 2 && Int32.TryParse(hz[0].Trim(), out hzone) && Int32.TryParse(hz[1].Trim(), out hch)) { int i = _channels.FindIndex(c => c.ZoneNumber == hzone && c.ZonePosition == hch); if (i >= 0) _homeIndex = i; }
                var zones = _channels.GroupBy(c => c.ZoneNumber).Select(g => "Z" + g.Key + "=" + g.Count()).ToArray();
                Summary = "Imported " + _channels.Count + " channels (" + String.Join(", ", zones) + ").\n" +
                          _channels.Count(c => c.RxOnly) + " receive-only, " + _channels.Count(c => c.ScanMember) + " scan members.\n" +
                          "Home: " + _channels[_homeIndex].Zone + " / " + _channels[_homeIndex].ZonePosition + " " + _channels[_homeIndex].Name + "\n" +
                          (_noMode.Checked ? "All channels use 1950 Hz as the PTT function tone. Set the TRC board to the No Mode Select tone table (S100-6 ON, S100-5/7/8 OFF).\n" : "Standard tone table: only Zone 1 channels 1-8 are tone-selectable.\n") +
                          (_busSelect.Checked ? "Channel select: SB9600 MODE/ZONE keys. LEARN your W7 zone buttons as ZONE UP / ZONE DN (or ZONE 1..n).\n" : "") +
                          (result.Warnings.Count > 0 ? "\nWarnings:\n" + String.Join("\n", result.Warnings.ToArray()) : "");
                return true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed"); return false; }
        }
        public void ApplyTo(RadioProfile radio)
        {
            radio.Channels.Clear(); radio.Channels.AddRange(_channels);
            radio.HomeChannel = _homeIndex; radio.SelectedChannel = Math.Min(radio.SelectedChannel, radio.Channels.Count - 1);
            if (_busSelect.Checked) radio.ChannelSelect = ChannelMethod.Sb9600ModeKeys;
            radio.Bus.SyncChannelFromDisplay = true;
            CpsImport.ApplyW7BenchKeymap(radio);
        }
    }

    // ---------------- quick key editor ----------------
    internal sealed class QuickKeyDialog : Form
    {
        public QuickKey Result;
        private readonly TextBox _label = new TextBox();
        private readonly HotkeyBox _hotkey = new HotkeyBox();
        private readonly Label _conflict = new Label();
        private readonly ComboBox _action = new ComboBox();
        private string _color;
        public QuickKeyDialog(QuickKey key, DispatchConfig config, RadioProfile radio)
        {
            Text = "Quick key"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(560, 300);
            _color = key.ColorHex ?? "";
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
            flow.Controls.Add(new Label { Text = "Label (shown on the key)", AutoSize = true });
            _label.Width = 520; _label.MaxLength = 24; _label.Text = key.Label; flow.Controls.Add(_label);
            flow.Controls.Add(new Label { Text = "Action - pick or type; join steps with ';' (e.g. chan:2; wait:400; alert:ATTENTION)", AutoSize = true });
            _action.Width = 520; _action.DropDownStyle = ComboBoxStyle.DropDown; _action.Text = key.Action;
            foreach (AlertProfile a in config.Alerts) { _action.Items.Add("alert:" + a.Name); _action.Items.Add("page:" + a.Name); }
            bool zoned = radio.Channels.Count > 0 && radio.Channels.TrueForAll(c => c.ZoneNumber > 0);
            for (int i = 0; i < radio.Channels.Count && i < 128; i++)
                _action.Items.Add(zoned ? "chan:Z" + radio.Channels[i].ZoneNumber + "/" + radio.Channels[i].ZonePosition : "chan:" + (i + 1));
            foreach (string s in new string[] { "func:MON", "func:SCAN", "func:NUIS DEL", "func:DIRECT", "func:LOW PWR", "func:SECURE", "func:BEACON", "func:ACK EMERG", "chan:+", "chan:-", "trc:MON", "trc:A", "trc:B", "trc:C", "trc:SECURE", "trc:CLEAR", "mute", "stopall", "replay:1", "dtmf:123#" }) _action.Items.Add(s);
            foreach (HeadButton b in radio.Bus.HeadButtons) _action.Items.Add("head:" + b.Name);
            foreach (RadioProfile r in config.Radios) _action.Items.Add("select:" + r.Name);
            flow.Controls.Add(_action);
            flow.Controls.Add(new Label { Text = "Keyboard key (click the box, then press the key or combination; Backspace clears)", AutoSize = true });
            _hotkey.Width = 240; Keys existing = Hotkey.Parse(key.Hotkey); _hotkey.Text = existing != Keys.None ? Hotkey.Format(existing) : "";
            _conflict.AutoSize = false; _conflict.Width = 520; _conflict.Height = 34; _conflict.ForeColor = Color.DarkRed;
            _hotkey.TextChanged += delegate { _conflict.Text = Conflict(config, key, Hotkey.Parse(_hotkey.Text)); };
            _conflict.Text = Conflict(config, key, existing);
            flow.Controls.Add(_hotkey); flow.Controls.Add(_conflict);
            ClientSize = new Size(560, 340);
            var colourRow = new FlowLayoutPanel { Width = 520, Height = 40 };
            var swatch = new Label { Width = 60, Height = 26, BorderStyle = BorderStyle.FixedSingle, BackColor = UiSettings.ParseHex(_color, SystemColors.Control) };
            colourRow.Controls.Add(DialogKit.Button("Colour...", 90, delegate { using (var cd = new ColorDialog { Color = swatch.BackColor, FullOpen = true }) if (cd.ShowDialog(this) == DialogResult.OK) { _color = UiSettings.ToHex(cd.Color); swatch.BackColor = cd.Color; } }));
            colourRow.Controls.Add(DialogKit.Button("Theme default", 110, delegate { _color = ""; swatch.BackColor = SystemColors.Control; }));
            colourRow.Controls.Add(swatch); flow.Controls.Add(colourRow);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var ok = DialogKit.Button("OK", 90, null); var cancel = DialogKit.Button("Cancel", 90, null); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate
            {
                if (_label.Text.Trim().Length == 0) { MessageBox.Show(this, "Enter a label."); return; }
                Result = new QuickKey(_label.Text.Trim(), _action.Text.Trim(), _color, _hotkey.Text.Trim()); DialogResult = DialogResult.OK; Close();
            };
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            Controls.Add(flow); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
        }
        // Names whatever else already uses this key, so two buttons never fight over one hotkey.
        private static string Conflict(DispatchConfig config, QuickKey self, Keys k)
        {
            if (k == Keys.None) return "";
            HotkeySettings h = config.Hotkeys;
            string[,] globals = { { h.Ptt, "PTT" }, { h.GroupPtt, "GROUP PTT" }, { h.StopAll, "STOP ALL" }, { h.ChannelUp, "CH +" }, { h.ChannelDown, "CH -" },
                                  { h.NextRadio, "RADIO >" }, { h.PreviousRadio, "< RADIO" }, { h.Monitor, "MONITOR" }, { h.ReplayLast, "REPLAY" } };
            for (int i = 0; i < globals.GetLength(0); i++) if (Hotkey.Parse(globals[i, 0]) == k) return Hotkey.Format(k) + " is already the " + globals[i, 1] + " key; that one will win. Pick another key.";
            foreach (QuickKey q in config.QuickKeys) if (!ReferenceEquals(q, self) && Hotkey.Parse(q.Hotkey) == k) return Hotkey.Format(k) + " is already used by quick key " + q.Label + ".";
            if (Hotkey.Reserved(k)) return Hotkey.Format(k) + " is reserved by Windows.";
            return "";
        }
    }

    // ---------------- appearance ----------------
    internal sealed class AppearanceDialog : Form
    {
        public UiSettings Result;
        public AppearanceDialog(UiSettings ui)
        {
            Result = ui; Text = "Appearance & layout"; StartPosition = FormStartPosition.CenterParent; Size = ConsoleWindow.FitToScreen(new Size(620, 720));
            var grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = ui, PropertySort = PropertySort.Alphabetical };
            var colours = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(6), WrapContents = true };
            colours.Controls.Add(new Label { Text = "Custom theme colours:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            foreach (string name in new string[] { "Back", "Panel", "Accent", "Text", "Warn", "Good", "Bad" })
            {
                string n = name; var b = DialogKit.Button(name, 70, null);
                b.BackColor = UiSettings.ParseHex(Get(ui, n), SystemColors.Control); b.ForeColor = UiTheme.ReadableOn(b.BackColor);
                b.Click += delegate
                {
                    using (var cd = new ColorDialog { Color = b.BackColor, FullOpen = true })
                        if (cd.ShowDialog(this) == DialogResult.OK) { Set(ui, n, UiSettings.ToHex(cd.Color)); ui.ThemeName = "Custom"; b.BackColor = cd.Color; b.ForeColor = UiTheme.ReadableOn(cd.Color); grid.Refresh(); }
                };
                colours.Controls.Add(b);
            }
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var ok = DialogKit.Button("Apply", 100, null); var cancel = DialogKit.Button("Cancel", 100, null); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += delegate { try { ui.Validate(); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check values"); } };
            footer.Controls.Add(ok); footer.Controls.Add(cancel);
            Controls.Add(grid); Controls.Add(colours); Controls.Add(footer); CancelButton = cancel;
        }
        private static string Get(UiSettings ui, string n)
        {
            switch (n) { case "Back": return ui.CustomBack; case "Panel": return ui.CustomPanel; case "Accent": return ui.CustomAccent; case "Text": return ui.CustomText; case "Warn": return ui.CustomWarn; case "Good": return ui.CustomGood; default: return ui.CustomBad; }
        }
        private static void Set(UiSettings ui, string n, string v)
        {
            switch (n) { case "Back": ui.CustomBack = v; break; case "Panel": ui.CustomPanel = v; break; case "Accent": ui.CustomAccent = v; break; case "Text": ui.CustomText = v; break; case "Warn": ui.CustomWarn = v; break; case "Good": ui.CustomGood = v; break; default: ui.CustomBad = v; break; }
        }
    }

    // ---------------- TX audio routing ----------------
    internal sealed class RoutingDialog : Form
    {
        private readonly ComboBox _mic = new ComboBox(), _out = new ComboBox(), _ptt = new ComboBox(), _chan = new ComboBox();
        private readonly CheckBox _notch = new CheckBox();
        private readonly ComboBox _jack = new ComboBox();
        public RoutingDialog(RadioProfile r)
        {
            Text = r.Name + " - transmit audio setup"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(640, 560); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
            flow.Controls.Add(new Label { Text = "TX output: the sound device wired (through a 600 ohm isolation transformer) to the consolette wireline,\r\nAccessory 2 pins 1/16 (LINE1, two-wire default) or 3/11 (LINE2, four-wire). Never your speakers.", Width = 600, Height = 40 });
            _out.Width = 600; _out.DropDownStyle = ComboBoxStyle.DropDownList; _out.Items.Add("");
            try { foreach (AudioDevice d in ReceiveAudio.Devices(false)) _out.Items.Add(d.Name); } catch (Exception) { }
            if (!_out.Items.Contains(r.PlannedTxOutput ?? "")) _out.Items.Add(r.PlannedTxOutput); _out.SelectedItem = r.PlannedTxOutput ?? "";
            flow.Controls.Add(_out);
            flow.Controls.Add(new Label { Text = "Microphone (desk mic, headset or footswitch-equipped mic):", Width = 600, Height = 22 });
            _mic.Width = 600; _mic.DropDownStyle = ComboBoxStyle.DropDownList; _mic.Items.Add("");
            try { foreach (AudioDevice d in ReceiveAudio.Devices(true)) _mic.Items.Add(d.Name); } catch (Exception) { }
            if (!_mic.Items.Contains(r.MicrophoneDevice ?? "")) _mic.Items.Add(r.MicrophoneDevice); _mic.SelectedItem = r.MicrophoneDevice ?? "";
            flow.Controls.Add(_mic);
            flow.Controls.Add(new Label { Text = "PTT method:", Width = 600, Height = 22 });
            _ptt.Width = 600; _ptt.DropDownStyle = ComboBoxStyle.DropDownList;
            _ptt.Items.Add("Tone remote: guard + mode function tone + low-level guard under voice (recommended)");
            _ptt.Items.Add("SB9600: press the head PTT button; voice-only audio on the TX output (check P103 / codeplug audio source)");
            _ptt.SelectedIndex = r.Ptt == PttMethod.TrcTones ? 0 : 1; flow.Controls.Add(_ptt);
            flow.Controls.Add(new Label { Text = "Channel select:", Width = 600, Height = 22 });
            _chan.Width = 600; _chan.DropDownStyle = ComboBoxStyle.DropDownList;
            _chan.Items.Add("Tone remote mode-select tone (8 modes on the standard tone table)");
            _chan.Items.Add("SB9600 MODE UP / MODE DN head keys (any number of modes; follows the radio's reported mode)");
            _chan.SelectedIndex = r.ChannelSelect == ChannelMethod.TrcTone ? 0 : 1; flow.Controls.Add(_chan);
 flow.Controls.Add(new Label { Text = "Which jack carries the radio TX audio:", Width = 600, Height = 22 });
            _jack.Width = 600; _jack.DropDownStyle = ComboBoxStyle.DropDownList;
            _jack.Items.Add("Whole device - a sound card used only for the radio (recommended)");
            _jack.Items.Add("Onboard 7.1: SIDE jack (grey) only - front jack stays for Windows/speakers");
            _jack.Items.Add("Onboard 7.1/5.1: REAR jack (black) only");
            _jack.Items.Add("Onboard 7.1/5.1: CENTER/BASS jack (orange) only");
            _jack.SelectedIndex = (int)r.TxOutputJack; flow.Controls.Add(_jack);
            _notch.Text = "Notch " + r.Tone.GuardHz + " Hz out of the microphone audio (recommended; the TRC board also notches it)"; _notch.Checked = r.MicNotch; _notch.Width = 600; flow.Controls.Add(_notch);
            flow.Controls.Add(new Label { Text = "After saving, use Audio > Levels / calibration to set the line level (guard tone = 0 dBm / 775 mVrms into 600 ohms).", Width = 600, Height = 36, ForeColor = Color.DarkRed });
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var ok = DialogKit.Button("Save", 90, null); ok.DialogResult = DialogResult.OK; var cancel = DialogKit.Button("Cancel", 90, null); cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            Controls.Add(flow); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
        }
        public void Apply(RadioProfile r)
        {
            r.PlannedTxOutput = (_out.SelectedItem as string) ?? ""; r.MicrophoneDevice = (_mic.SelectedItem as string) ?? "";
            r.Ptt = _ptt.SelectedIndex == 1 ? PttMethod.Sb9600Button : PttMethod.TrcTones;
            r.ChannelSelect = _chan.SelectedIndex == 1 ? ChannelMethod.Sb9600ModeKeys : ChannelMethod.TrcTone;
            r.MicNotch = _notch.Checked; r.TxOutputJack = (TxJack)Math.Max(0, _jack.SelectedIndex);
        }
    }

    // ---------------- level calibration ----------------
    internal sealed class CalibrationDialog : Form
    {
        private readonly RadioProfile _r; private readonly RadioLink _link; private readonly Func<string, bool> _allowed; private readonly Action<string> _log;
        private readonly Label _status = new Label(); private readonly Timer _timer = new Timer();
        public CalibrationDialog(RadioProfile radio, RadioLink link, Func<string, bool> allowed, Action<string> log)
        {
            _r = radio; _link = link; _allowed = allowed; _log = log;
            Text = radio.Name + " - line levels and calibration"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(700, 560); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            ToneProfile t = radio.Tone; double scale = radio.ToneLevel / 100.0;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12), AutoScroll = true };
            flow.Controls.Add(new Label
            {
                Width = 660, Height = 170,
                Text = "Target levels (XTL5000 consolette manual, TRC adjustment procedure):\r\n" +
                       "  - High-level guard tone (HLGT) at the consolette: 0 dBm = 775 mVrms across 600 ohms. The TRC AGC normalises to this.\r\n" +
                       "  - Low-level guard tone: 30 dB below HLGT (configured " + (t.HoldDbfs - t.GuardDbfs).ToString("0") + " dB).\r\n" +
                       "  - Factory TX check: 120 mVrms 1 kHz into LINE2 (4-wire) gives 3 kHz deviation with AGC enabled.\r\n" +
                       "  - RX: the board is set for 0 dBm on LINE1 with a 1 kHz, 3 kHz-deviation signal.\r\n\r\n" +
                       "Method: connect a true-RMS meter or scope across the line (or a 600 ohm load), play HLGT STEADY and adjust the Windows\r\n" +
                       "output volume for the TX device until it reads 775 mVrms. Then leave Windows volume alone and trim with TONE REMOTE.\r\n" +
                       "Steady tones contain no function tone, so the consolette does not key; TEST KEY does key the transmitter."
            });
            _status.Width = 660; _status.Height = 44; _status.ForeColor = Color.DarkBlue;
            _status.Text = "Digital levels now: HLGT " + Db(t.GuardDbfs, scale) + "   function " + Db(t.FunctionDbfs, scale) + "   LLGT " + Db(t.HoldDbfs, scale) + "  (TONE REMOTE " + radio.ToneLevel + "%)";
            flow.Controls.Add(_status);
            var row1 = new FlowLayoutPanel { Width = 660, Height = 42 };
            row1.Controls.Add(DialogKit.Button("HLGT " + t.GuardHz + " Hz STEADY", 190, delegate { Cal(t.GuardHz, t.GuardDbfs, "HLGT steady"); }));
            row1.Controls.Add(DialogKit.Button("1000 Hz at HLGT level", 170, delegate { Cal(1000, t.GuardDbfs, "1 kHz at HLGT level"); }));
            row1.Controls.Add(DialogKit.Button("LLGT STEADY", 120, delegate { Cal(t.GuardHz, t.HoldDbfs, "LLGT steady"); }));
            row1.Controls.Add(DialogKit.Button("FUNCTION " + radio.Channels[radio.SelectedChannel].FunctionHz + " Hz", 150, delegate { Cal(radio.Channels[radio.SelectedChannel].FunctionHz, t.FunctionDbfs, "function tone steady"); }));
            flow.Controls.Add(row1);
            var row2 = new FlowLayoutPanel { Width = 660, Height = 42 };
            row2.Controls.Add(DialogKit.Button("1000 Hz sound-card check", 190, delegate { Cal(1000, -12, "1000 Hz frequency check (counter should read 1000 Hz)"); }));
            row2.Controls.Add(DialogKit.Button("TEST KEY 3 s (keys TX!)", 190, delegate { TestKey(); }));
            var stop = DialogKit.Button("STOP", 100, delegate { if (_link != null && _link.Trc != null) { _link.Trc.Abort(); _log("Calibration output stopped."); } });
            stop.BackColor = Color.FromArgb(220, 120, 120); row2.Controls.Add(stop);
            flow.Controls.Add(row2);
            flow.Controls.Add(new Label { Width = 660, Height = 60, Text = "Steady tones stop automatically after 15 s. Requires Live (Settings > Operating mode) and a TX output device for this radio (and ARM TX if RequireArm is on).\r\nIf the HLGT cannot reach 775 mVrms at full Windows volume, raise TONE REMOTE or reduce attenuation in your interface; keep the output below clipping (meter in Audio > Mixer)." });
            var close = DialogKit.Button("Close", 100, null); close.DialogResult = DialogResult.Cancel; flow.Controls.Add(close); CancelButton = close;
            Controls.Add(flow);
            _timer.Interval = 200; _timer.Tick += delegate { if (_link != null && _link.Trc != null) Text = _r.Name + " - calibration   [" + _link.Trc.Phase + "  peak " + (_link.Trc.OutPeak <= 0 ? "-inf" : (20 * Math.Log10(_link.Trc.OutPeak / 32768.0)).ToString("0.0")) + " dBFS]"; };
            _timer.Start();
            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); if (_link != null && _link.Trc != null && !_link.Keyed) _link.Trc.Abort(); };
        }
        private static string Db(double dbfs, double scale) { return (dbfs + 20 * Math.Log10(Math.Max(0.0001, scale))).ToString("0.0") + " dBFS"; }
        private bool Ready(string what)
        {
            if (!_allowed(what)) return false;
            if (_link == null || !_link.TrcOpen) { _log(_r.Name + ": TRC output not open (Live mode and a TX output device are required)."); return false; }
            if (_link.Trc.Active) { _link.Trc.Abort(); System.Threading.Thread.Sleep(30); }
            return true;
        }
        private void Cal(double hz, double dbfs, string what)
        {
            if (!Ready("CALIBRATION " + what)) return;
            if (_link.Trc.BeginCalibration(hz, dbfs, _r.ToneLevel / 100.0, 15000)) _log(_r.Name + ": calibration " + what + " (" + hz + " Hz, " + Db(dbfs, _r.ToneLevel / 100.0) + ") for 15 s.");
        }
        private void TestKey()
        {
            if (MessageBox.Show(this, "This keys the transmitter for about 3 seconds with a 1 kHz tone. Use a dummy load or an authorised test channel.", "Test key", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (!Ready("TEST KEY")) return;
            var tone = ConsoleWave.Alert(new AlertProfile { Kind = AlertKind.Steady, FrequencyA = 1000, DurationMs = 3000 }, 60);
            var p = new TrcParams { Tone = _r.Tone, FunctionHz = _r.Channels[_r.SelectedChannel].FunctionHz, ToneScale = _r.ToneLevel / 100.0, Program = tone, UseMic = false, MaxMs = 5000, ProgramGain = 1.0 };
            if (_link.Trc.BeginKeyed(p)) _log(_r.Name + ": TEST KEY - guard, " + p.FunctionHz + " Hz function tone, 3 s of 1 kHz with low-level guard.");
        }
    }

    // ---------------- SB9600 bus monitor + LEARN ----------------
    internal sealed class BusMonitorForm : Form
    {
        private readonly ConsoleWindow _owner;
        private readonly ComboBox _radio = new ComboBox();
        private readonly ListView _list = new ListView();
        private readonly ListBox _learned = new ListBox();
        private readonly CheckBox _pause = new CheckBox(), _hideLights = new CheckBox(), _hideTx = new CheckBox(), _learn = new CheckBox();
        private readonly Label _lines = new Label();
        private readonly TextBox _send = new TextBox();
        private readonly Timer _timer = new Timer();
        private sealed class Learned { public int Code; public string Frame; public DateTime Time; public override string ToString() { return Time.ToString("HH:mm:ss") + "   control 0x" + Code.ToString("X2") + "   [" + Frame + "]"; } }

        public BusMonitorForm(ConsoleWindow owner)
        {
            _owner = owner; Text = "SB9600 bus monitor / LEARN"; Size = ConsoleWindow.FitToScreen(new Size(1100, 760)); StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(800, 500);
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4), WrapContents = false };
            _radio.DropDownStyle = ComboBoxStyle.DropDownList; _radio.Width = 220; foreach (RadioProfile r in owner.MonitorRadios()) _radio.Items.Add(r);
            _radio.SelectedItem = owner.MonitorSelectedRadio(); if (_radio.SelectedIndex < 0 && _radio.Items.Count > 0) _radio.SelectedIndex = 0;
            top.Controls.Add(_radio);
            _pause.Text = "Pause"; _pause.AutoSize = true; _hideLights.Text = "Hide illumination"; _hideLights.Checked = true; _hideLights.AutoSize = true; _hideTx.Text = "Hide our TX"; _hideTx.AutoSize = true;
            _learn.Text = "LEARN head buttons"; _learn.AutoSize = true; _learn.Font = new Font(Font, FontStyle.Bold);
            foreach (CheckBox c in new CheckBox[] { _pause, _hideLights, _hideTx, _learn }) { c.Margin = new Padding(8, 8, 4, 0); top.Controls.Add(c); }
            top.Controls.Add(DialogKit.Button("Clear", 70, delegate { _list.Items.Clear(); }));
            top.Controls.Add(DialogKit.Button("Save...", 70, delegate { SaveLog(); }));
            top.Controls.Add(DialogKit.Button("BUSY test", 90, delegate { RadioProfile r = Radio(); if (r != null) _owner.MonitorBusyTest(r); }));
            top.Controls.Add(DialogKit.Button("DTR", 50, delegate { RadioProfile r = Radio(); if (r != null) _owner.MonitorToggleLine(r, "DTR"); }));
            top.Controls.Add(DialogKit.Button("RTS", 50, delegate { RadioProfile r = Radio(); if (r != null) _owner.MonitorToggleLine(r, "RTS"); }));
            _lines.Dock = DockStyle.Top; _lines.Height = 24; _lines.Font = new Font("Consolas", 9F); _lines.Padding = new Padding(6, 4, 0, 0);
            _list.Dock = DockStyle.Fill; _list.View = View.Details; _list.FullRowSelect = true; _list.Font = new Font("Consolas", 9F); _list.HideSelection = false;
            _list.Columns.Add("Time", 100); _list.Columns.Add("Dir", 44); _list.Columns.Add("Decoded", 900);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 210, ColumnCount = 2, RowCount = 1 };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var learnPanel = new Panel { Dock = DockStyle.Fill };
            _learned.Dock = DockStyle.Fill; _learned.Font = new Font("Consolas", 9F); _learned.IntegralHeight = false;
            var learnBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 76 };
            learnBar.Controls.Add(DialogKit.Button("Save as head button...", 170, delegate { SaveLearned(); }));
            learnBar.Controls.Add(DialogKit.Button("Make quick key...", 140, delegate { QuickFromLearned(); }));
            learnBar.Controls.Add(DialogKit.Button("Clear", 70, delegate { _learned.Items.Clear(); }));
            var learnTitle = new Label { Dock = DockStyle.Top, Height = 34, Text = "LEARN: tick LEARN, press a key on the W7 head, then name it. Captured controls appear here.", Padding = new Padding(4) };
            learnPanel.Controls.Add(_learned); learnPanel.Controls.Add(learnBar); learnPanel.Controls.Add(learnTitle);
            var sendPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6) };
            sendPanel.Controls.Add(new Label { Text = "Send a frame (4 hex bytes; CRC is added). Example: 05 61 02 57 = HOME toggle.", Width = 500, Height = 22 });
            _send.Width = 300; _send.Font = new Font("Consolas", 10F); _send.Text = "05 61 02 57"; sendPanel.Controls.Add(_send);
            var sendRow = new FlowLayoutPanel { Width = 520, Height = 40 };
            sendRow.Controls.Add(DialogKit.Button("SEND", 80, delegate { RadioProfile r = Radio(); if (r != null) _owner.MonitorSend(r, _send.Text); }));
            sendRow.Controls.Add(DialogKit.Button("Rule from selected row...", 190, delegate { RuleFromSelected(); }));
            sendRow.Controls.Add(DialogKit.Button("Copy row", 90, delegate { if (_list.SelectedItems.Count > 0) Clipboard.SetText(_list.SelectedItems[0].SubItems[2].Text); }));
            sendPanel.Controls.Add(sendRow);
            sendPanel.Controls.Add(new Label { Text = "Sending needs the radio's SB9600 'Allow transmit' setting. PTT frames also need Live mode.", Width = 500, Height = 40, ForeColor = Color.DarkRed });
            bottom.Controls.Add(learnPanel, 0, 0); bottom.Controls.Add(sendPanel, 1, 0);
            Controls.Add(_list); Controls.Add(bottom); Controls.Add(_lines); Controls.Add(top);
            _timer.Interval = 300; _timer.Tick += delegate { RadioProfile r = Radio(); _lines.Text = r == null ? "" : "Lines: " + _owner.MonitorLineStatus(r); };
            _timer.Start();
            FormClosed += delegate { _timer.Stop(); _timer.Dispose(); };
        }
        private RadioProfile Radio() { return _radio.SelectedItem as RadioProfile; }

        public void Append(RadioProfile radio, SbItem item, string text)
        {
            if (radio != Radio()) return;
            bool lighting = item.Frame != null && item.Frame.Addr == 0x05 && item.Frame.Func == 0x58;
            if (_learn.Checked && item.Kind == SbItem.Kinds.Rx && item.Frame.Addr == 0x05 && item.Frame.Func == 0x57 && item.Frame.P1 != 0x02 && (item.Frame.P2 == 1 || item.Frame.P2 == 2))
            {
                bool seen = false; foreach (object o in _learned.Items) if (((Learned)o).Code == item.Frame.P1) seen = true;
                if (!seen) { _learned.Items.Add(new Learned { Code = item.Frame.P1, Frame = item.Frame.ToString(), Time = DateTime.Now }); _learned.SelectedIndex = _learned.Items.Count - 1; }
            }
            if (_pause.Checked || (lighting && _hideLights.Checked) || (item.Kind == SbItem.Kinds.Tx && _hideTx.Checked)) return;
            var row = new ListViewItem(item.Time.ToString("HH:mm:ss.fff"));
            row.SubItems.Add(item.Kind == SbItem.Kinds.Tx ? "TX" : item.Kind == SbItem.Kinds.Rx || item.Kind == SbItem.Kinds.Sbep ? "RX" : item.Kind.ToString().ToUpperInvariant());
            row.SubItems.Add(text.Length > 4 ? text.Substring(4) : text); row.Tag = item;
            if (item.Kind == SbItem.Kinds.Tx) row.ForeColor = Color.DarkBlue; else if (item.Kind == SbItem.Kinds.Error) row.ForeColor = Color.DarkRed; else if (item.Kind == SbItem.Kinds.Raw) row.ForeColor = Color.Gray;
            _list.BeginUpdate();
            _list.Items.Add(row);
            while (_list.Items.Count > 4000) _list.Items.RemoveAt(0);
            _list.EndUpdate();
            row.EnsureVisible();
        }
        private void SaveLearned()
        {
            var item = _learned.SelectedItem as Learned; RadioProfile r = Radio(); if (item == null || r == null) { MessageBox.Show(this, "Select a captured control first."); return; }
            using (var dialog = new LabelDialog("HEAD BUTTON NAME", "TOP1"))
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.ResultText.Trim().Length > 0) _owner.MonitorAddHeadButton(r, dialog.ResultText.Trim().ToUpperInvariant(), item.Code);
        }
        private void QuickFromLearned()
        {
            var item = _learned.SelectedItem as Learned; RadioProfile r = Radio(); if (item == null || r == null) { MessageBox.Show(this, "Select a captured control first."); return; }
            using (var dialog = new LabelDialog("QUICK KEY LABEL", "W7 0x" + item.Code.ToString("X2")))
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.ResultText.Trim().Length > 0)
                    _owner.MonitorAddQuickKey(dialog.ResultText.Trim(), "sb:05 " + item.Code.ToString("X2") + " 01 57; wait:120; sb:05 " + item.Code.ToString("X2") + " 00 57");
        }
        private void RuleFromSelected()
        {
            RadioProfile r = Radio(); if (r == null || _list.SelectedItems.Count == 0) { MessageBox.Show(this, "Select a received frame in the list first."); return; }
            var item = _list.SelectedItems[0].Tag as SbItem; if (item == null || item.Frame == null) { MessageBox.Show(this, "Pick an SB9600 frame row (not SBEP/raw)."); return; }
            using (var form = new Form { Text = "New bus rule", Size = new Size(460, 230), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog })
            {
                var name = new TextBox { Left = 12, Top = 30, Width = 420, Text = "Rule " + item.Frame.ToString() };
                var pattern = new TextBox { Left = 12, Top = 80, Width = 200, Text = item.Frame.Addr.ToString("X2") + " " + item.Frame.P1.ToString("X2") + " " + item.Frame.P2.ToString("X2") + " " + item.Frame.Func.ToString("X2") };
                var ev = new ComboBox { Left = 230, Top = 80, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList }; foreach (RuleEvent e in Enum.GetValues(typeof(RuleEvent))) ev.Items.Add(e); ev.SelectedItem = RuleEvent.Emergency;
                var ok = new Button { Text = "Add rule", Left = 320, Top = 130, Width = 110, DialogResult = DialogResult.OK };
                form.Controls.Add(new Label { Text = "Name", Left = 12, Top = 10, Width = 200 }); form.Controls.Add(name);
                form.Controls.Add(new Label { Text = "Pattern (** = any)", Left = 12, Top = 60, Width = 200 }); form.Controls.Add(pattern);
                form.Controls.Add(new Label { Text = "Event", Left = 230, Top = 60, Width = 200 }); form.Controls.Add(ev); form.Controls.Add(ok); form.AcceptButton = ok;
                if (form.ShowDialog(this) != DialogResult.OK) return;
                byte[] v, m; if (!SbPattern.TryParse(pattern.Text, out v, out m)) { MessageBox.Show(this, "Pattern must be four hex bytes or **."); return; }
                _owner.MonitorAddRule(r, new BusRule(name.Text.Trim().Length > 0 ? name.Text.Trim() : "Rule", pattern.Text.Trim().ToUpperInvariant(), (RuleEvent)ev.SelectedItem));
            }
        }
        private void SaveLog()
        {
            var sb = new StringBuilder();
            foreach (ListViewItem row in _list.Items) sb.AppendLine(row.Text + "  " + row.SubItems[1].Text + "  " + row.SubItems[2].Text);
            using (var picker = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "SB9600_capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" })
                if (picker.ShowDialog(this) == DialogResult.OK) try { File.WriteAllText(picker.FileName, sb.ToString()); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        }
    }

    internal sealed class TextViewer : Form
    {
        public TextViewer(string title, string text)
        {
            Text = title; Size = ConsoleWindow.FitToScreen(new Size(980, 720)); StartPosition = FormStartPosition.CenterParent;
            var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Font = new Font("Consolas", 9.5F), Text = text, BackColor = Color.White };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
            var close = DialogKit.Button("Close", 90, null); close.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(close); bar.Controls.Add(DialogKit.Button("Copy all", 90, delegate { Clipboard.SetText(box.Text); }));
            Controls.Add(box); Controls.Add(bar); CancelButton = close;
            Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; };
        }
    }
}
