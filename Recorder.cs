using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

// Instant-recall and logging recorder.
// RX audio is captured from the live RX monitor stream and opens a "call" when it exceeds the threshold
// (or while the SB9600 bus reports the radio's audio unmuted). TX audio is what the console actually sent.
// Calls are kept in memory (16 kHz mono) for instant recall, and optionally written to WAV + CSV index.
namespace RadioDispatchConsole
{
    internal sealed class CallSegment
    {
        public string RadioId, RadioName, Kind, FilePath = "", Channel = "";
        public DateTime Start;
        public short[] Pcm;
        public double Seconds { get { return Pcm == null ? 0 : Pcm.Length / (double)CallRecorder.Rate; } }
        public override string ToString() { return Start.ToString("HH:mm:ss") + "  " + Kind + "  " + (Channel.Length > 0 ? Channel : RadioName) + "  " + Seconds.ToString("0.0") + " s" + (FilePath.Length > 0 ? "  (saved)" : ""); }
    }

    internal sealed class CallRecorder
    {
        public const int Rate = 16000;
        private sealed class Track
        {
            public string Id, Name, Kind; public bool Open, Carrier; public DateTime Start; public long LastActive;
            public readonly List<short> Data = new List<short>();
            public readonly List<short> PreRoll = new List<short>();
            public int Phase; public int Acc;
        }
        private readonly object _lock = new object();
        private readonly List<CallSegment> _calls = new List<CallSegment>();
        private readonly Dictionary<string, Track> _rx = new Dictionary<string, Track>(), _tx = new Dictionary<string, Track>();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private long _total;
        public RecorderSettings Settings = new RecorderSettings();
        public string BaseFolder = AppDomain.CurrentDomain.BaseDirectory;
        public string LastError = "";
        public int Version;   // bumps whenever the call list changes
        // (radio id, "RX"/"TX", call start) -> channel label for the recording; set by the console.
        public Func<string, string, DateTime, string> ChannelOf;

        public List<CallSegment> Calls() { lock (_lock) { return new List<CallSegment>(_calls); } }
        public void Clear() { lock (_lock) { _calls.Clear(); _total = 0; Version++; } }

        // 48 kHz -> 16 kHz by averaging groups of three samples (adequate for voice recall).
        private static void Decimate(Track t, short[] mono48, int n, List<short> dst)
        {
            for (int i = 0; i < n; i++)
            {
                t.Acc += mono48[i]; t.Phase++;
                if (t.Phase == 3) { dst.Add((short)(t.Acc / 3)); t.Acc = 0; t.Phase = 0; }
            }
        }

        public void SetCarrier(string id, string name, bool carrier)
        {
            lock (_lock) { Track t = Get(_rx, id, name, "RX"); t.Carrier = carrier; if (carrier) t.LastActive = _clock.ElapsedMilliseconds; }
        }

        public void AddRx(string id, string name, short[] mono48, int n)
        {
            if (n <= 0) return;
            double sum = 0; for (int i = 0; i < n; i++) sum += (double)mono48[i] * mono48[i];
            double rms = Math.Sqrt(sum / n), db = rms <= 0 ? -120 : 20 * Math.Log10(rms / 32768.0);
            CallSegment done = null;
            lock (_lock)
            {
                Track t = Get(_rx, id, name, "RX"); long now = _clock.ElapsedMilliseconds;
                bool active = db >= Settings.ThresholdDb || t.Carrier;
                if (active) t.LastActive = now;
                if (!t.Open)
                {
                    Decimate(t, mono48, n, t.PreRoll);
                    if (t.PreRoll.Count > Rate * 3 / 10) t.PreRoll.RemoveRange(0, t.PreRoll.Count - Rate * 3 / 10);
                    if (active) { t.Open = true; t.Start = DateTime.Now.AddMilliseconds(-300); t.Data.Clear(); t.Data.AddRange(t.PreRoll); t.PreRoll.Clear(); }
                    return;
                }
                Decimate(t, mono48, n, t.Data);
                if ((!active && now - t.LastActive > Settings.HangMs) || t.Data.Count > Rate * 600) done = CloseLocked(t);
            }
            if (done != null) Persist(done);
        }

        public void BeginTx(string id, string name)
        {
            lock (_lock) { Track t = Get(_tx, id, name, "TX"); t.Open = true; t.Start = DateTime.Now; t.Data.Clear(); t.Phase = 0; t.Acc = 0; }
        }
        // Called on the audio thread.
        public void AddTx(string id, short[] mono48, int n)
        {
            lock (_lock)
            {
                Track t; if (!_tx.TryGetValue(id, out t) || !t.Open) return;
                if (t.Data.Count < Rate * 600) Decimate(t, mono48, n, t.Data);
            }
        }
        public void EndTx(string id)
        {
            CallSegment done = null;
            lock (_lock) { Track t; if (_tx.TryGetValue(id, out t) && t.Open) done = CloseLocked(t); }
            if (done != null) Persist(done);
        }

        // Closes RX tracks whose hang time expired even when no audio is arriving (monitor stopped).
        public void Poll()
        {
            var done = new List<CallSegment>();
            lock (_lock)
            {
                long now = _clock.ElapsedMilliseconds;
                foreach (Track t in _rx.Values) if (t.Open && !t.Carrier && now - t.LastActive > Settings.HangMs + 500) { CallSegment c = CloseLocked(t); if (c != null) done.Add(c); }
            }
            foreach (CallSegment c in done) Persist(c);
        }

        private Track Get(Dictionary<string, Track> map, string id, string name, string kind)
        {
            Track t; if (!map.TryGetValue(id, out t)) { t = new Track { Id = id, Name = name, Kind = kind }; map[id] = t; }
            t.Name = name; return t;
        }

        private CallSegment CloseLocked(Track t)
        {
            t.Open = false;
            if (t.Data.Count < Rate / 4) { t.Data.Clear(); return null; }   // ignore < 250 ms blips
            var call = new CallSegment { RadioId = t.Id, RadioName = t.Name, Kind = t.Kind, Start = t.Start, Pcm = t.Data.ToArray() };
            try { if (ChannelOf != null) call.Channel = ChannelOf(t.Id, t.Kind, t.Start) ?? ""; } catch (Exception) { call.Channel = ""; }
            t.Data.Clear();
            _calls.Add(call); _total += call.Pcm.Length;
            long cap = (long)Settings.RecallMinutes * 60 * Rate;
            while (_total > cap && _calls.Count > 1) { _total -= _calls[0].Pcm.Length; _calls.RemoveAt(0); }
            Version++;
            return call;
        }

        private void Persist(CallSegment call)
        {
            if (!Settings.DiskLogging) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string root = String.IsNullOrWhiteSpace(Settings.LogFolder) ? Path.Combine(BaseFolder, "Recordings") : Settings.LogFolder;
                    string day = Path.Combine(root, call.Start.ToString("yyyy-MM-dd")); Directory.CreateDirectory(day);
                    string file = Path.Combine(day, call.Start.ToString("HHmmss_fff") + "_" + Safe(call.Channel.Length > 0 ? call.Channel : call.RadioName) + "_" + call.Kind + ".wav");
                    ConsoleWave.Write(file, call.Pcm, Rate);
                    string index = Path.Combine(day, "index.csv");
                    // channel is the last column, so index files started by older versions still line up
                    if (!File.Exists(index)) File.AppendAllText(index, "start,radio,direction,seconds,file,channel" + Environment.NewLine, Encoding.UTF8);
                    File.AppendAllText(index, call.Start.ToString("s") + "," + Csv(call.RadioName) + "," + call.Kind + "," + call.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," + Csv(Path.GetFileName(file)) + "," + Csv(call.Channel) + Environment.NewLine, Encoding.UTF8);
                    lock (_lock) { call.FilePath = file; Version++; }
                }
                catch (Exception ex) { LastError = "Recorder could not save: " + ex.Message; }
            });
        }
        // Deletes recordings older than KeepDays. Only day folders the recorder itself makes (named yyyy-MM-dd) are touched,
        // and only the .wav files and index.csv inside them; a folder is removed once it is empty. Returns files deleted.
        public static int Purge(string root, int keepDays, DateTime today)
        {
            if (keepDays <= 0 || String.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
            DateTime cutoff = today.Date.AddDays(-keepDays); int deleted = 0;
            foreach (string dir in Directory.GetDirectories(root))
            {
                DateTime day;
                if (!DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day) || day >= cutoff) continue;
                foreach (string file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (!name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && !String.Equals(name, "index.csv", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); deleted++; } catch (Exception) { }
                }
                try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch (Exception) { }
            }
            return deleted;
        }
        private static string Safe(string s)
        {
            var sb = new StringBuilder(); foreach (char c in s ?? "") sb.Append(Char.IsLetterOrDigit(c) ? c : '_');
            string r = sb.ToString(); return r.Length > 30 ? r.Substring(0, 30) : r;
        }
        private static string Csv(string s) { s = s ?? ""; return s.IndexOfAny(new char[] { ',', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }

        // Concatenates calls with 400 ms of silence between them for playback.
        public static short[] Join(List<CallSegment> calls)
        {
            var all = new List<short>();
            foreach (CallSegment c in calls) { if (all.Count > 0) all.AddRange(new short[Rate * 2 / 5]); all.AddRange(c.Pcm); }
            return all.ToArray();
        }
    }
}
