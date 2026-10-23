using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    internal sealed class AudioPane : Panel
    {
        private readonly ComboBox _input = new ComboBox(), _output = new ComboBox();
        private readonly TrackBar _volume = new TrackBar();
        private readonly CheckBox _mute = new CheckBox();
        private readonly Button _start = new Button(), _refresh = new Button();
        private readonly ProgressBar _meter = new ProgressBar();
        private readonly Label _state, _level, _volumeText;
        private readonly Timer _poll = new Timer();
        private readonly Action<string> _log;
        private readonly string _path;
        private ReceiveAudio _engine;
        private string _savedInput = "", _savedOutput = "";
        private int _savedVolume = 70;
        private bool _initializing = true, _savedMute;
        public Action<bool, string> StatusChanged;
        public Action<int, bool> PreferencesChanged;
        public Action<short[], int> RxTap;
        public bool Monitoring { get { return _engine != null && _engine.Running; } }
        public int LivePeak { get { return _engine == null ? 0 : _engine.Peak; } }
        private bool _duck;
        // Silences the monitor while this console transmits (RX mute on TX) without changing the saved mute.
        public bool Duck { get { return _duck; } set { _duck = value; if (_engine != null) _engine.Muted = _mute.Checked || _duck; } }
        public void SetMonitor(int volume, bool mute)
        { _volume.Value = Math.Max(0, Math.Min(100, volume)); _mute.Checked = mute; }
        public AudioPane(Action<string> logger, string settingsPath)
        {
            _log = logger; _path = settingsPath; ReadSettings();
            BackColor = Color.FromArgb(24, 39, 56);
            BorderStyle = BorderStyle.FixedSingle;
            AutoScroll = true; AutoScrollMinSize = new Size(0, 650);
            TextAt("OPERATOR AUDIO", 16, 15, 35, true);
            TextAt("RECEIVE AUDIO AND RECORDING", 9, 50, 25, true);
            TextAt("Receive input (isolated line-in)", 9, 85, 24, false);
            SetupPicker(_input, 112);
            TextAt("Operator speakers / headphones", 9, 153, 24, false);
            SetupPicker(_output, 180);
            SetupButton(_refresh, "REFRESH DEVICES", 219, 34);
            _refresh.Click += delegate { RefreshDevices(); };
            TextAt("Monitor volume", 9, 265, 24, false);
            _volume.SetBounds(12, 292, 260, 46); _volume.Minimum = 0; _volume.Maximum = 100;
            _volume.TickFrequency = 10; _volume.Value = Math.Max(0, Math.Min(100, _savedVolume));
            Controls.Add(_volume);
            _volumeText = TextAt(_volume.Value + "%", 10, 337, 24, true);
            _mute.Text = "MUTE MONITOR"; _mute.ForeColor = Color.White; _mute.Checked = _savedMute;
            _mute.SetBounds(19, 366, 260, 29); Controls.Add(_mute);
            _meter.SetBounds(19, 405, 260, 19); _meter.Maximum = 32768; Controls.Add(_meter);
            _level = TextAt("INPUT LEVEL: STOPPED", 9, 428, 24, false);
            SetupButton(_start, "START RX MONITOR", 463, 42);
            _state = TextAt("RX MONITOR STOPPED", 9, 514, 44, true);
            TextAt("Choose an operator output that is separate from\nthe radio TX feed. In LIVE, monitoring and recording\nstart automatically on these devices.", 8.5F, 563, 75, false);
            _start.Click += delegate { if (_engine == null) StartMonitor(false); else StopMonitor(); };
            _volume.ValueChanged += delegate { _volumeText.Text = _volume.Value + "%"; ApplySettings(); };
            _mute.CheckedChanged += delegate { ApplySettings(); };
            _input.SelectedIndexChanged += delegate { SaveSettings(); };
            _output.SelectedIndexChanged += delegate { SaveSettings(); };
            _poll.Interval = 15;
            _poll.Tick += delegate
            {
                try
                {
                    _engine.Pump();
                    _meter.Value = _engine.Peak;
                    _level.Text = _engine.Peak >= 32000 ? "INPUT NEAR CLIPPING - LOWER LINE-IN" :
                        "INPUT: " + (_engine.Peak == 0 ? "SILENCE" : (20 * Math.Log10(_engine.Peak / 32768.0)).ToString("0.0") + " dBFS");
                    _state.Text = (_mute.Checked ? "RX MONITOR MUTED" : "RX MONITOR RUNNING") + "\nDropped buffers: " + _engine.DroppedBlocks;
                }
                catch (Exception ex) { _log("RX monitor error: " + ex.Message); StopMonitor(); }
            };
            Resize += delegate
            {
                int width = Math.Max(180, ClientSize.Width - 40);
                foreach (Control c in Controls) c.Width = width;
            };
            RefreshDevices(); _initializing = false;
        }
        private Label TextAt(string text, float size, int top, int height, bool bold)
        {
            var label = DispatchWindow.MakeLabel(text, size, Color.FromArgb(218, 233, 244), bold);
            label.SetBounds(19, top, 270, height); Controls.Add(label); return label;
        }
        private void SetupPicker(ComboBox picker, int top)
        {
            picker.DropDownStyle = ComboBoxStyle.DropDownList; picker.SetBounds(19, top, 260, 30); Controls.Add(picker);
        }
        private void SetupButton(Button button, string text, int top, int height)
        {
            button.Text = text; button.SetBounds(19, top, 260, height); button.ForeColor = Color.White;
            button.BackColor = Color.FromArgb(42, 70, 92); button.FlatStyle = FlatStyle.Flat; Controls.Add(button);
        }
        private void RefreshDevices()
        {
            bool previous = _initializing; _initializing = true;
            try
            {
                string input = SelectedName(_input), output = SelectedName(_output);
                Fill(_input, true, input.Length > 0 ? input : _savedInput);
                Fill(_output, false, output.Length > 0 ? output : _savedOutput);
            }
            catch (Exception ex) { _log("Device enumeration failed: " + ex.Message); }
            finally { _initializing = previous; }
        }
        private static void Fill(ComboBox picker, bool input, string saved)
        {
            picker.Items.Clear(); picker.Items.Add(new AudioDevice(-1, input ? "Select receive input..." : "Select operator output..."));
            picker.SelectedIndex = 0;
            foreach (AudioDevice device in ReceiveAudio.Devices(input)) picker.Items.Add(device);
            // Only restore a unique name; legacy WinMM names may be truncated or duplicated.
            int match = -1;
            for (int i = 1; i < picker.Items.Count; i++)
                if (((AudioDevice)picker.Items[i]).Name == saved) { if (match != -1) { match = -1; break; } match = i; }
            if (match > 0) picker.SelectedIndex = match;
        }
        private static string SelectedName(ComboBox picker)
        {
            AudioDevice device = picker.SelectedItem as AudioDevice;
            return device == null || device.Id < 0 ? "" : device.Name;
        }
        // Starts on the saved devices with no dialogs (LIVE start-up). Returns why it could not start, or "" when running.
        public string AutoStart()
        {
            if (Monitoring) return "";
            AudioDevice input = _input.SelectedItem as AudioDevice, output = _output.SelectedItem as AudioDevice;
            if (input == null || output == null || input.Id < 0 || output.Id < 0) return "no receive input / operator output chosen yet (Audio > Receive audio)";
            return StartMonitor(true) ? "" : "the saved receive devices could not be started (see the journal)";
        }
        private void Problem(bool quiet, string text, string title) { if (quiet) _log(title + ": " + text); else MessageBox.Show(this, text, title); }
        private bool StartMonitor(bool quiet)
        {
            AudioDevice input = _input.SelectedItem as AudioDevice, output = _output.SelectedItem as AudioDevice;
            if (input == null || output == null || input.Id < 0 || output.Id < 0)
            { Problem(quiet, "Choose the receive input and operator playback output first.", "Select audio devices"); return false; }
            // IDs can shift after hotplug. Refuse stale routing rather than silently retargeting it.
            try
            {
                if (!StillPresent(input, true) || !StillPresent(output, false))
                { Problem(quiet, "The device list changed. Refresh devices and select the routing again.", "Audio devices changed"); return false; }
            }
            catch (Exception ex) { Problem(quiet, ex.Message, "Audio device check failed"); return false; }
            _engine = new ReceiveAudio { Volume = _volume.Value, Muted = _mute.Checked, Tap = RxTap };
            try
            {
                _engine.Start(input.Id, output.Id);
                _input.Enabled = _output.Enabled = _refresh.Enabled = false;
                _start.Text = "STOP RX MONITOR"; _poll.Start(); SaveSettings();
                Notify(true); _log("RX monitoring started: " + input + " -> " + output + ". Receive audio is being recorded.");
                return true;
            }
            catch (Exception ex)
            {
                StopMonitor(); _log("Monitor start failed: " + ex.Message);
                if (!quiet) MessageBox.Show(this, ex.Message, "RX monitor could not start");
                return false;
            }
        }
        private static bool StillPresent(AudioDevice selected, bool input)
        {
            foreach (AudioDevice device in ReceiveAudio.Devices(input))
                if (device.Id == selected.Id && device.Name == selected.Name) return true;
            return false;
        }
        private void Notify(bool running)
        {
            if (StatusChanged != null) StatusChanged(running, _mute.Checked ? "MUTED" : "RUNNING");
        }
        private void ApplySettings()
        {
            if (_engine != null) { _engine.Volume = _volume.Value; _engine.Muted = _mute.Checked || _duck; Notify(true); }
            SaveSettings();
            if (!_initializing && PreferencesChanged != null) PreferencesChanged(_volume.Value, _mute.Checked);
        }
        public void StopMonitor()
        {
            _poll.Stop(); bool failed = false;
            if (_engine != null)
            {
                try { _engine.Dispose(); _engine = null; }
                catch (Exception ex) { failed = true; _log("Audio cleanup failed; retry STOP or close app: " + ex.Message); }
            }
            _input.Enabled = _output.Enabled = _refresh.Enabled = !failed;
            _start.Text = failed ? "RETRY STOP" : "START RX MONITOR"; _meter.Value = 0;
            _level.Text = "INPUT LEVEL: STOPPED";
            _state.Text = failed ? "AUDIO ERROR - RETRY STOP" : "RX MONITOR STOPPED";
            Notify(false);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { StopMonitor(); _poll.Dispose(); }
            base.Dispose(disposing);
        }
        private void ReadSettings()
        {
            try
            {
                if (!File.Exists(_path)) return;
                foreach (string line in File.ReadAllLines(_path))
                {
                    int split = line.IndexOf('='); if (split < 0) continue;
                    string key = line.Substring(0, split), value = line.Substring(split + 1); int n;
                    if (key == "monitor_input") _savedInput = value;
                    if (key == "monitor_device") _savedOutput = value;
                    if (key == "monitor_volume" && int.TryParse(value, out n)) _savedVolume = n;
                    if (key == "monitor_mute") _savedMute = value == "true";
                }
            }
            catch (Exception ex) { _log("Audio preferences unavailable: " + ex.Message); }
        }
        private void SaveSettings()
        {
            if (_initializing) return;
            try
            {
                File.WriteAllLines(_path, new string[] { "monitor_input=" + SelectedName(_input), "monitor_device=" + SelectedName(_output),
                    "monitor_volume=" + _volume.Value, "monitor_mute=" + (_mute.Checked ? "true" : "false") });
            }
            catch (Exception ex) { _log("Audio preference save failed: " + ex.Message); }
        }
    }
}
