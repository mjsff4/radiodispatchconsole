using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    public sealed partial class ConsoleWindow
    {
        private readonly List<string> _traceLines = new List<string>();

        // ---------------- CONTROL: zone / channel keys ----------------
        // Radio functions (MON, SCAN, NUIS DEL, FUNC A-C ...) are not repeated here: they are quick keys on the bar at the
        // bottom of the screen (right-click a quick key to edit it, + adds one), so each control has exactly one button.
        private Control BuildControlTab()
        {
            var table = new TableLayoutPanel { RowCount = 3, ColumnCount = 1, BackColor = _t.Panel, Padding = new Padding(_t.S(_compact ? 4 : 8)) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, _compact ? 0 : _t.S(22)));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, _compact ? 0 : _t.S(30)));
            Label h2 = Heading("ZONE / CHANNEL KEYS", 900); h2.Visible = !_compact;
            _tips.SetToolTip(h2, "Click a key to select that channel. Over SB9600 the console changes the radio and confirms it from the radio display."); h2.Dock = DockStyle.Fill; table.Controls.Add(h2, 0, 0);
            _channelKeys = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            _channelKeys.Resize += delegate
            {
                if (_channelKeys == null || _channelKeys.Controls.Count == 0) return;
                int w = ChannelKeyWidth();
                foreach (Control c in _channelKeys.Controls) if (c.Tag is int) c.Width = w;
            };
            table.Controls.Add(_channelKeys, 0, 1);
            Label tip = Note("Dimmed keys are RX ONLY and are never keyed. Radio functions (MON, SCAN, NUIS DEL) are the quick keys at the bottom.", 900, 28); tip.Dock = DockStyle.Fill; tip.AutoEllipsis = true; tip.Visible = !_compact;
            table.Controls.Add(tip, 0, 2);
            return table;
        }
        // Up to ChannelKeyColumns columns, but never narrower than a readable full channel name.
        private int ChannelKeyWidth()
        {
            if (_channelKeys == null) return _t.S(190);
            int avail = _channelKeys.ClientSize.Width - _t.S(24) - SystemInformation.VerticalScrollBarWidth;
            int minW = _t.S(_compact ? 160 : 190);
            int cols = Math.Max(1, Math.Min(Math.Max(1, _config.Ui.ChannelKeyColumns), avail / minW));
            return Math.Max(_t.S(_compact ? 140 : 150), avail / cols - _t.S(6));
        }
        // How many lines a wrapping row of fixed-size buttons needs at a given width.
        private static int FlowRows(Control.ControlCollection items, int width)
        {
            int lines = 1, x = 0;
            foreach (Control c in items)
            {
                int cw = c.Width + c.Margin.Horizontal;
                if (x > 0 && x + cw > width) { lines++; x = 0; }
                x += cw;
            }
            return lines;
        }

        // ---------------- ALERTS / PAGING ----------------
        private Control BuildAlerts()
        {
            var content = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, BackColor = _t.Panel, Padding = new Padding(_t.S(8)) };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            FlowLayoutPanel left = Stack(); left.Padding = new Padding(0);
            Label alertHead = Heading("ALERT / TONE KEYS", 520); left.Controls.Add(alertHead);
            _tips.SetToolTip(alertHead, "Alerts and tone sets are sent in Live (Test mode only simulates them). Add tone sets in Settings > Console settings > Alerts / paging > Add tone set; ShowOnMain gives one a key here.");
            _alertButtons = new FlowLayoutPanel { Size = new Size(_t.S(540), (_t.ButtonH + _t.S(8)) * 3), WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            left.Controls.Add(_alertButtons);
            _presets = Combo(_t.S(360)); left.Controls.Add(_presets);
            FlowLayoutPanel actions = Row(560);
            actions.Controls.Add(_t.MakeButton("SEND", delegate { SendAlert(); }, 90)); actions.Controls.Add(_t.MakeButton("+ QUEUE", delegate { Enqueue(); }, 96));
            actions.Controls.Add(_t.MakeButton("LISTEN", delegate { Audition(); }, 90)); actions.Controls.Add(_t.MakeButton("STOP", delegate { StopPreview(); }, 76));
            left.Controls.Add(actions);
            left.Controls.Add(Heading("DTMF", 520));
            FlowLayoutPanel dtmf = Row(560);
            _digits = new TextBox { Width = _t.S(200), Text = "123#", MaxLength = 32, Font = _t.Mono, BackColor = _t.Back, ForeColor = _t.Text, Margin = new Padding(_t.S(3), _t.S(8), _t.S(3), 0) };
            dtmf.Controls.Add(_digits); dtmf.Controls.Add(_t.MakeButton("SEND DTMF", delegate { Dtmf(false); }, 116));
            left.Controls.Add(dtmf);
            left.Controls.Add(Heading("OFFLINE TEST FILES", 520));
            FlowLayoutPanel files = Row(560);
            files.Controls.Add(_t.MakeButton("TRC SEQUENCE WAV", delegate { ExportTrc(); }, 170)); files.Controls.Add(_t.MakeButton("CTCSS TEST WAV", delegate { ExportCtcss(); }, 150));
            left.Controls.Add(files);
            left.Controls.Add(Note("Two-tone defaults are test frequencies only. Enter your department's verified tone pairs in Settings > Console settings > Alerts / paging. LISTEN plays on the operator speaker (Console settings > Console > OperatorOutputDevice), never a radio input.", 540, 44));
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Color.Transparent, Padding = new Padding(_t.S(8), 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(26))); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, (_t.ButtonH + _t.S(8)) * 2)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(40)));
            Label qh = Heading("PAGING QUEUE", 300); qh.Dock = DockStyle.Fill; right.Controls.Add(qh, 0, 0);
            _queueList = new ListBox { Dock = DockStyle.Fill, BackColor = _t.Back, ForeColor = _t.Text, Font = _t.Base, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
            right.Controls.Add(_queueList, 0, 1);
            var qb = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, BackColor = Color.Transparent };
            qb.Controls.Add(_t.MakeButton("START", delegate { StartQueue(); }, 90)); qb.Controls.Add(_t.MakeButton("CANCEL", delegate { _queueRunning = false; StopTx("Queue cancelled"); RefreshQueue(); }, 90));
            qb.Controls.Add(_t.MakeButton("REMOVE", delegate { if (!_queueRunning && _queueList.SelectedIndex >= 0) { _queue.RemoveAt(_queueList.SelectedIndex); RefreshQueue(); } }, 90));
            qb.Controls.Add(_t.MakeButton("CLEAR", delegate { _queueRunning = false; StopTx("Queue cleared"); _queue.Clear(); RefreshQueue(); }, 90));
            right.Controls.Add(qb, 0, 2);
            _queueState = _t.MakeLabel("", _t.Small, _t.Warn); _queueState.Dock = DockStyle.Fill; right.Controls.Add(_queueState, 0, 3);
            content.Controls.Add(left, 0, 0); content.Controls.Add(right, 1, 0);
            return content;
        }

        // ---------------- HEAD: SB9600 virtual control head ----------------
        private Control BuildHead()
        {
            FlowLayoutPanel flow = Stack();
            _headMirror = _t.MakeLabel("", _t.Mono, _t.Warn); _headMirror.Size = new Size(_t.S(900), _t.S(22)); flow.Controls.Add(_headMirror);
            flow.Controls.Add(Heading("CUSTOM RADIO ACTIONS  (edit in Settings > Console settings > radio tab > Softkeys)", 900));
            _softButtons = new FlowLayoutPanel { Size = new Size(_t.S(900), (_t.ButtonH + _t.S(8)) * 2), WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            flow.Controls.Add(_softButtons);
            flow.Controls.Add(Heading("W7 HEAD CONTROLS OVER SB9600  (click = press + release; right-click = toggle / hold)", 900));
            _headButtons = new FlowLayoutPanel { Size = new Size(_t.S(900), (_t.ButtonH + _t.S(8)) * 4), WrapContents = true, AutoScroll = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            flow.Controls.Add(_headButtons);
            flow.Controls.Add(Heading("CONSOLE MENU / KEYPAD  (simulation menu; keypad digits press head keys 0-9 * # when live)", 900));
            FlowLayoutPanel nav = Row(900);
            foreach (string action in new string[] { "MENU", "LEFT", "RIGHT", "SELECT", "HOME", "BACK", "CALL", "PAGE", "RSSI" }) { string key = action; nav.Controls.Add(_t.MakeButton(key, delegate { HeadKey(key); }, 86)); }
            flow.Controls.Add(nav);
            var keypad = new FlowLayoutPanel { Size = new Size(_t.S(330), (_t.ButtonH + _t.S(8)) * 3), WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            foreach (char digit in "123456789*0#") { string key = digit.ToString(); keypad.Controls.Add(_t.MakeButton(key, delegate { HeadKey(key); }, 96)); }
            flow.Controls.Add(keypad);
            flow.Controls.Add(Heading("SIMULATION EVENTS", 900));
            FlowLayoutPanel inject = Row(900);
            inject.Controls.Add(_t.MakeButton("INJECT RX", delegate { Inject("RX"); }, 110)); inject.Controls.Add(_t.MakeButton("INJECT CALLER", delegate { Inject("CALLER"); }, 140));
            inject.Controls.Add(_t.MakeButton("INJECT EMERGENCY", delegate { Inject("EMERGENCY"); }, 170)); inject.Controls.Add(_t.MakeButton("CLEAR RX", delegate { Inject("CLEAR"); }, 110));
            inject.Controls.Add(_t.MakeButton("SB9600 MONITOR / LEARN", delegate { OpenBusMonitor(); }, 190));
            flow.Controls.Add(inject);
            flow.Controls.Add(Heading("RADIO DISPLAY NAMES  (turn the W7 to a channel, then link its short text to the full name)", 900));
            FlowLayoutPanel names = Row(900);
            names.Controls.Add(_t.MakeButton("LINK DISPLAY TO SELECTED CHANNEL", delegate { LinkDisplayToChannel(); }, 300));
            names.Controls.Add(_t.MakeButton("ADD TEXT LOOKUP...", delegate { AddDisplayLookup(); }, 180));
            flow.Controls.Add(names);
            flow.Controls.Add(Note("Example: the W7 shows CITY FD. Select channel 'City Fire Department' in the console and click LINK. From then on, whenever the radio shows CITY FD the console selects that channel and shows the full name. Lookups expand any other short text (zones, talkgroups, status). Edit both in Settings > Console settings > radio tab > Channels / modes (RadioText) and Display lookups.", 900, 44));
            return flow;
        }

        // ---------------- MIXER ----------------
        private Control BuildMixer()
        {
            FlowLayoutPanel flow = Stack();
            _mixerNote = _t.MakeLabel("", _t.Small, _t.Warn); _mixerNote.Size = new Size(_t.S(760), _t.S(52)); _mixerNote.TextAlign = ContentAlignment.TopLeft; flow.Controls.Add(_mixerNote);
            foreach (string name in new string[] { "RX MONITOR", "MIC / VOICE", "ALERT / PAGING", "TONE REMOTE" })
            {
                string key = name; var row = new FlowLayoutPanel { Width = _t.S(760), Height = _t.S(46), WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
                Label label = _t.MakeLabel(name, _t.Bold, _t.Text); label.Size = new Size(_t.S(150), _t.S(36));
                var slider = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = _t.S(420), Height = _t.S(42), BackColor = _t.Panel };
                Label value = _t.MakeLabel("", _t.Bold, _t.Accent); value.Size = new Size(_t.S(70), _t.S(36));
                _sliders[key] = slider; _sliderLabels[key] = value;
                slider.ValueChanged += delegate { if (_refreshing) return; SetLevel(key, slider.Value); };
                row.Controls.Add(label); row.Controls.Add(slider); row.Controls.Add(value); flow.Controls.Add(row);
            }
            flow.Controls.Add(Heading("LEVELS", 760));
            _rxMeter = AddMeter(flow, "RX (line in)", _t.Good); _txMeter = AddMeter(flow, "TX (to line)", _t.Bad); _micMeter = AddMeter(flow, "MIC", _t.Accent);
            FlowLayoutPanel actions = Row(760);
            actions.Controls.Add(_t.MakeButton("RECEIVE AUDIO...", delegate { OpenAudio(); }, 150));
            actions.Controls.Add(_t.MakeButton("TX ROUTING...", delegate { ConfigureRouting(); }, 170));
            actions.Controls.Add(_t.MakeButton("LEVELS / CALIBRATION...", delegate { OpenCalibration(); }, 150));
            actions.Controls.Add(_t.MakeButton("MUTE SELECTED", delegate { ToggleMute(_selected); }, 150));
            flow.Controls.Add(actions);
            FlowLayoutPanel gates = Row(760);
            gates.Controls.Add(_t.MakeButton("RX MUTE ON TX", delegate { _selected.MuteRxOnTx = !_selected.MuteRxOnTx; Save(); RefreshSelected(); }, 160));
            gates.Controls.Add(_t.MakeButton("MIC GUARD NOTCH", delegate { _selected.MicNotch = !_selected.MicNotch; Save(); RefreshSelected(); }, 170));
            flow.Controls.Add(gates);
            flow.Controls.Add(Note("RX audio comes from the live RX panel (consolette LINE1 through an isolation transformer into a PC line input). TX audio leaves on the TX output you choose (into LINE1 for two-wire or LINE2 for four-wire). TONE REMOTE scales guard/function tones and therefore the voice ceiling; MIC / VOICE drives the voice limiter.", 760, 58));
            return flow;
        }
        private LevelMeter AddMeter(FlowLayoutPanel flow, string label, Color color)
        {
            var row = new FlowLayoutPanel { Width = _t.S(760), Height = _t.S(26), WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            Label l = _t.MakeLabel(label, _t.Small, _t.Text); l.Size = new Size(_t.S(150), _t.S(20)); row.Controls.Add(l);
            var meter = new LevelMeter(_t, color) { Size = new Size(_t.S(420), _t.S(14)), Margin = new Padding(_t.S(3), _t.S(4), _t.S(3), 0) };
            row.Controls.Add(meter); flow.Controls.Add(row); return meter;
        }

        // ---------------- RECORDER / instant recall ----------------
        private Control BuildRecorder()
        {
            var table = new TableLayoutPanel { RowCount = 4, ColumnCount = 1, BackColor = _t.Panel, Padding = new Padding(_t.S(8)) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.ButtonH + _t.S(10)));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.ButtonH + _t.S(10)));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, _t.S(40)));
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent };
            Label pl = _t.MakeLabel("PLAY ON:", _t.Bold, _t.Text); pl.Size = new Size(_t.S(80), _t.ButtonH); top.Controls.Add(pl);
            _playDevice = Combo(_t.S(330));
            try { foreach (AudioDevice d in ReceiveAudio.Devices(false)) _playDevice.Items.Add(d); } catch (Exception ex) { Log("Output device list unavailable: " + ex.Message); }
            foreach (object item in _playDevice.Items) if (((AudioDevice)item).Name == _config.OperatorOutputDevice) _playDevice.SelectedItem = item;
            _playDevice.SelectedIndexChanged += delegate { AudioDevice d = _playDevice.SelectedItem as AudioDevice; if (d != null) { _config.OperatorOutputDevice = d.Name; Save(); } };
            top.Controls.Add(_playDevice);
            var disk = new CheckBox { Text = "LOG EVERY CALL TO DISK (WAV + CSV)", Checked = _config.Recorder.DiskLogging, ForeColor = _t.Text, Font = _t.Bold, AutoSize = true, Margin = new Padding(_t.S(12), _t.S(8), 0, 0) };
            disk.CheckedChanged += delegate { _config.Recorder.DiskLogging = disk.Checked; Save(); Log("Disk logging " + (disk.Checked ? "ON: " + RecordingFolder() : "OFF")); };
            top.Controls.Add(disk);
            table.Controls.Add(top, 0, 0);
            _calls = new ListBox { Dock = DockStyle.Fill, BackColor = _t.Back, ForeColor = _t.Text, Font = _t.Mono, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
            _calls.DoubleClick += delegate { PlaySelectedCalls(); };
            table.Controls.Add(_calls, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent };
            buttons.Controls.Add(_t.MakeButton("PLAY SELECTED", delegate { PlaySelectedCalls(); }, 140));
            buttons.Controls.Add(_t.MakeButton("LAST CALL", delegate { ReplayLast(1); }, 110));
            buttons.Controls.Add(_t.MakeButton("LAST 5", delegate { ReplayLast(5); }, 90));
            buttons.Controls.Add(_t.MakeButton("STOP", delegate { StopPreview(); }, 80));
            buttons.Controls.Add(_t.MakeButton("SAVE WAV...", delegate { SaveSelectedCalls(); }, 120));
            buttons.Controls.Add(_t.MakeButton("OPEN FOLDER", delegate { OpenRecordingFolder(); }, 130));
            buttons.Controls.Add(_t.MakeButton("CLEAR", delegate { _recorder.Clear(); }, 80));
            table.Controls.Add(buttons, 0, 2);
            _recStatus = Note("RX calls are captured while the receive audio panel (Audio > Receive audio) is running (and while the SB9600 bus reports the radio unmuted). TX calls are what this console transmitted. " + _config.Recorder.RecallMinutes + " minutes are kept in memory.", 900, 36);
            _recStatus.Dock = DockStyle.Fill; table.Controls.Add(_recStatus, 0, 3);
            return table;
        }
        private string RecordingFolder() { return String.IsNullOrWhiteSpace(_config.Recorder.LogFolder) ? Path.Combine(_data, "Recordings") : _config.Recorder.LogFolder; }
        private void OpenRecordingFolder()
        {
            try { string f = RecordingFolder(); Directory.CreateDirectory(f); Process.Start("explorer.exe", "\"" + f + "\""); } catch (Exception ex) { Log("Cannot open folder: " + ex.Message); }
        }
        private void RefreshCalls()
        {
            _callsVersion = _recorder.Version;
            if (_calls == null || _calls.IsDisposed) return;
            List<CallSegment> calls = _recorder.Calls(); calls.Reverse();
            _calls.BeginUpdate(); _calls.Items.Clear(); foreach (CallSegment c in calls) _calls.Items.Add(c); _calls.EndUpdate();
        }
        private int OperatorDevice()
        {
            try
            {
                foreach (AudioDevice d in ReceiveAudio.Devices(false)) if (d.Name == _config.OperatorOutputDevice) return d.Id;
            }
            catch (Exception) { }
            return -1;
        }
        private void PlayPcm(short[] pcm, int rate, string what)
        {
            int device = OperatorDevice();
            if (device < 0) { Log("Choose the operator speaker (PLAY ON in Audio > Recorder, or Settings > Console settings > Console > OperatorOutputDevice) first."); SelectTab("RECORDER"); return; }
            StopPreview();
            try { _preview = new LocalPreview(); _preview.Start(device, pcm, rate); Log("Playing " + what + " on " + _config.OperatorOutputDevice + "."); }
            catch (Exception ex) { Log("Playback failed: " + ex.Message); StopPreview(); }
        }
        private void PlaySelectedCalls()
        {
            if (_calls == null) return;
            var list = _calls.SelectedItems.Cast<CallSegment>().OrderBy(c => c.Start).ToList();
            if (list.Count == 0) { ReplayLast(1); return; }
            PlayPcm(CallRecorder.Join(list), CallRecorder.Rate, list.Count + " recalled call(s)");
        }
        private void ReplayLast(int count)
        {
            List<CallSegment> calls = _recorder.Calls();
            if (calls.Count == 0) { Log("Instant recall is empty."); return; }
            List<CallSegment> last = calls.Skip(Math.Max(0, calls.Count - count)).ToList();
            PlayPcm(CallRecorder.Join(last), CallRecorder.Rate, "last " + last.Count + " call(s)");
        }
        private void SaveSelectedCalls()
        {
            if (_calls == null) return;
            var list = _calls.SelectedItems.Cast<CallSegment>().OrderBy(c => c.Start).ToList();
            if (list.Count == 0) { Log("Select calls to save."); return; }
            using (var picker = new SaveFileDialog { Filter = "PCM WAV (*.wav)|*.wav", FileName = "Recall_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav" })
                if (picker.ShowDialog(this) == DialogResult.OK) try { ConsoleWave.Write(picker.FileName, CallRecorder.Join(list), CallRecorder.Rate); Log("Saved " + picker.FileName); } catch (Exception ex) { Log("Save failed: " + ex.Message); }
        }

        // ---------------- DIAGNOSTICS ----------------
        private Control BuildDiagnostics()
        {
            var tabs = new ThemedTabs(_t);
            var tracePanel = new Panel { Dock = DockStyle.Fill, BackColor = _t.Panel };
            _trace = MakeText(true); _trace.Text = String.Join(Environment.NewLine, _traceLines.ToArray()) + (_traceLines.Count > 0 ? Environment.NewLine : "");
            tracePanel.Controls.Add(_trace);
            var traceActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = _t.ButtonH + _t.S(10), BackColor = Color.Transparent };
            traceActions.Controls.Add(_t.MakeButton("CLEAR", delegate { _trace.Clear(); _traceLines.Clear(); }, 90));
            traceActions.Controls.Add(_t.MakeButton("LIST COM PORTS", delegate { ListPorts(); }, 150));
            traceActions.Controls.Add(_t.MakeButton("SAVE TRACE", delegate { SaveText(_trace.Text, "Dispatch_Trace.txt"); }, 120));
            traceActions.Controls.Add(_t.MakeButton("SB9600 MONITOR", delegate { OpenBusMonitor(); }, 130));
            tracePanel.Controls.Add(traceActions);
            AddPage(tabs, "Control trace", tracePanel);
            var rawPanel = new Panel { Dock = DockStyle.Fill, BackColor = _t.Panel };
            _raw = MakeText(true); rawPanel.Controls.Add(_raw);
            var rawActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = _t.ButtonH + _t.S(10), BackColor = Color.Transparent };
            rawActions.Controls.Add(_t.MakeButton("OPEN CAPTURE TEXT", delegate { OpenCapture(); }, 180));
            rawActions.Controls.Add(_t.MakeButton("DECODE HEX", delegate { DecodeRaw(); }, 130));
            rawPanel.Controls.Add(rawActions);
            AddPage(tabs, "Capture decoder", rawPanel);
            FlowLayoutPanel notes = Stack();
            notes.Controls.Add(Heading("Operator incident / dispatch note", 600));
            _note = new TextBox { Multiline = true, Size = new Size(_t.S(600), _t.S(130)), BackColor = _t.Back, ForeColor = _t.Text, Font = _t.Base };
            notes.Controls.Add(_note);
            notes.Controls.Add(_t.MakeButton("ADD TO JOURNAL", delegate { if (_note.Text.Trim().Length > 0) { Log("NOTE: " + _note.Text.Trim().Replace("\r", "").Replace("\n", " / ")); _note.Clear(); } }, 180));
            AddPage(tabs, "Operator notes", notes);
            return tabs;
        }
        private TextBox MakeText(bool readOnly)
        {
            return new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = _t.Back, ForeColor = _t.Good, Font = _t.Mono };
        }
        private void AddPage(TabControl tabs, string title, Control content)
        {
            var page = new TabPage(title) { BackColor = _t.Panel }; content.Dock = DockStyle.Fill; page.Controls.Add(content); tabs.TabPages.Add(page);
        }
        private void OpenCapture()
        {
            using (var picker = new OpenFileDialog { Filter = "Capture text (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*" })
                if (picker.ShowDialog(this) == DialogResult.OK) try
                {
                    if (new FileInfo(picker.FileName).Length > 2000000) { Log("Capture viewer limit: 2 MB."); return; }
                    _raw.ReadOnly = false; _raw.Text = File.ReadAllText(picker.FileName); Log("Loaded capture text. Use DECODE HEX to decode SB9600 frames in it.");
                }
                catch (Exception ex) { Log(ex.Message); }
        }
        // Decodes every hex byte found in the capture text with the same parser the live bus uses.
        private void DecodeRaw()
        {
            if (_raw == null) return;
            var bytes = new List<byte>();
            foreach (string token in _raw.Text.Split(new char[] { ' ', '\t', '\r', '\n', ',', ';', '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token.Substring(2) : token; int n;
                if (t.Length == 2 && Int32.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out n)) bytes.Add((byte)n);
            }
            if (bytes.Count == 0) { Log("No hex bytes found in the capture text."); return; }
            var lines = new List<string>();
            using (var link = new Sb9600Link())
            {
                link.Feed(bytes.ToArray(), bytes.Count); link.FlushPending();
                foreach (SbItem item in link.Drain()) lines.Add(DescribeItem(item, _selected));
            }
            _raw.Text = _raw.Text + Environment.NewLine + "---- decoded " + bytes.Count + " bytes ----" + Environment.NewLine + String.Join(Environment.NewLine, lines.ToArray());
        }
    }
}
