using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    // Per-radio hardware connection: SB9600 bus (through the RIB) and tone-remote audio engine.
    internal sealed class RadioLink : IDisposable
    {
        public readonly RadioProfile Radio;
        public readonly Sb9600Link Bus = new Sb9600Link();
        public TrcEngine Trc;
        public string BusError = "", TrcError = "", LastJob = "";
        public bool BusKeyed, Keyed, FaultLogged, PortDown;
        public RadioLink(RadioProfile radio) { Radio = radio; }
        public bool BusOpen { get { return Bus.IsOpen; } }
        public bool TrcOpen { get { return Trc != null && Trc.IsOpen; } }
        public string BusText()
        {
            if (BusOpen) return "SB9600 " + Bus.PortLabel + " rx " + Bus.RxFrames + "/tx " + Bus.TxFrames + (Bus.TransmitAllowed ? "" : " LISTEN");
            return "SB9600 " + (BusError.Length > 0 ? "ERROR" : "off");
        }
        public string TrcText()
        {
            if (TrcOpen) return "TRC " + Trc.Phase + (Trc.Underruns > 0 ? " mic-gaps " + Trc.Underruns : "");
            return "TRC " + (TrcError.Length > 0 ? "ERROR" : "off");
        }
        public string Summary(ConsoleWindow.OpMode mode)
        {
            if (mode == ConsoleWindow.OpMode.Simulation) return "SIM  " + (BusOpen ? "bus model" : "");
            return BusText() + "   " + TrcText();
        }
        public void Dispose() { if (Trc != null) { Trc.Close(); Trc = null; } Bus.Close(); }
    }

    public sealed partial class ConsoleWindow
    {
        private readonly Dictionary<string, RadioLink> _links = new Dictionary<string, RadioLink>();
        private sealed class MacroStep { public string Action = ""; public int WaitMs; public string RadioId = ""; }
        private readonly List<MacroStep> _macro = new List<MacroStep>();
        private DateTime _macroDue = DateTime.MinValue;
        private BusMonitorForm _busMonitor;
        private static readonly string[] MenuOptions = { "SCAN", "MON", "DIRECT", "LOW PWR", "SECURE", "BEACON" };

        private RadioLink Link(RadioProfile radio) { RadioLink l; return radio != null && _links.TryGetValue(radio.Id, out l) ? l : null; }
        private RadioProfile RadioById(string id) { return _config.Radios.FirstOrDefault(r => r.Id == id); }

        // =====================================================================
        // Mode / arming / connections
        // =====================================================================
        private void SetMode(OpMode mode)
        {
            StopAll("Mode changed"); _armed = false;
            if (mode == OpMode.Simulation) CloseAudio();
            _opMode = mode;
            OpenLinksForMode();
            // Normal operation needs no ARM step: PTT must still be held, and STOP ALL / the PTT timeout always apply.
            if (mode == OpMode.Live && !_config.RequireArm) _armed = true;
            RefreshModeMenu(); RefreshSelected();
            if (mode == OpMode.Simulation) Log("TEST MODE: simulated radio and SB9600 bus. Nothing is sent to the radio. Settings > Operating mode > Live returns to normal operation.");
            else if (mode == OpMode.LiveReceive) Log("RECEIVE ONLY: SB9600 control and receive audio; nothing is transmitted.");
            else Log(_config.RequireArm ? "LIVE: radio audio open (silent). Press ARM TX before transmitting." : "LIVE: ready. Hold PTT or " + Hotkey.Display(_config.Hotkeys.Ptt) + " to talk.");
            // At start-up the main window does not exist yet; receive audio then starts when it is first shown.
            if (mode != OpMode.Simulation && _config.Recorder.AutoStartRx) { if (Visible) StartReceiveAudio(); else _rxStartPending = true; }
        }
        private bool _rxStartPending;

        private void ToggleArm()
        {
            if (_opMode != OpMode.Live) { Log("Choose Settings > Operating mode > Live first."); return; }
            if (_armed) { StopAll("TX disarmed"); _armed = false; Log("TX SAFE. Tone and voice output inhibited."); return; }
            bool any = _config.Radios.Any(r => { RadioLink l = Link(r); return l != null && (l.TrcOpen || (r.Ptt == PttMethod.Sb9600Button && l.BusOpen && l.Bus.TransmitAllowed)); });
            if (!any) Log("WARNING: no radio has an open TRC output or SB9600 PTT path. Set the TX output device (Audio > TX routing) or the RIB port.");
            if (_config.ConfirmLiveTx && !_armConfirmed)
            {
                string msg = "ARM TX sends tone-remote tones and microphone audio to the consolette, which can key a transmitter on the air.\n\n" +
                    "Before the first live test:\n  - operate only on frequencies you are licensed/authorised to use\n  - use a dummy load or a monitored test channel\n  - calibrate levels (Audio > Levels / calibration) so the guard tone reads 0 dBm at the consolette\n  - confirm the TX output device is the radio interface, not your speakers\n\nArm TX now?";
                if (MessageBox.Show(this, msg, "Arm live transmit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                _armConfirmed = true;
            }
            _armed = true; Log("TX ARMED. PTT, alerts, paging and function tones will be sent. Press TX ARMED again or " + _config.Hotkeys.StopAll + " to stop.");
        }

        private bool TxAllowed(string what)
        {
            if (_opMode == OpMode.Simulation) return true;
            if (_opMode == OpMode.LiveReceive) { Log("NOT SENT (" + what + "): Receive only mode (Settings > Operating mode)."); return false; }
            if (!_armed) { Log("NOT SENT (" + what + "): press ARM TX first (Settings > Console settings > RequireArm is on)."); return false; }
            return true;
        }

        private void OpenLinksForMode()
        {
            CloseLinks();
            foreach (RadioProfile radio in _config.Radios)
            {
                var link = new RadioLink(radio); _links[radio.Id] = link;
                RadioState st; if (_states.TryGetValue(radio.Id, out st)) { st.BusDisplay = ""; st.BusDisplay2 = ""; st.BusChannel = -1; st.BusTx = false; st.BusRx = false; st.Known.Clear(); }
                link.Bus.SimChannels = radio.Channels.Select(c => String.IsNullOrWhiteSpace(c.RadioText) ? c.Name : c.RadioText).ToList();
                if (radio.Channels.All(c => c.ZoneNumber > 0))
                {
                    link.Bus.SimZones = radio.Channels.Select(c => c.ZoneNumber).ToList();
                    link.Bus.SimZoneUp = SbDecoder.ButtonCode("ZONE UP", radio.Bus.HeadButtons); link.Bus.SimZoneDown = SbDecoder.ButtonCode("ZONE DN", radio.Bus.HeadButtons);
                    foreach (int z in radio.Channels.Select(c => c.ZoneNumber).Distinct()) { int code = SbDecoder.ButtonCode("ZONE " + z, radio.Bus.HeadButtons); if (code >= 0) link.Bus.SimZoneDirect[code] = z; }
                }
                link.Bus.SimHome = radio.HomeChannel;
                if (_opMode == OpMode.Simulation) { link.Bus.OpenSimulated(radio.Bus); continue; }
                if (!String.IsNullOrEmpty(radio.Bus.PortName))
                {
                    string err = link.Bus.Open(radio.Bus);
                    if (err != null) { link.BusError = err; link.PortDown = true; Log(radio.Name + ": " + err + " The console keeps retrying every 3 s."); }
                    else if (radio.Bus.SyncOnConnect && radio.Bus.AllowTransmit && radio == _selected) ScheduleSync(2500);
                }
                else Log(radio.Name + ": no RIB COM port set (Settings > Console settings > radio tab > SB9600 / RIB). Bus readback off.");
                if (_opMode == OpMode.Live) OpenTrc(link);
            }
            RefreshCards();
        }

        private void OpenTrc(RadioLink link)
        {
            RadioProfile r = link.Radio;
            if (String.IsNullOrEmpty(r.PlannedTxOutput)) { link.TrcError = "no TX output"; Log(r.Name + ": no TX output device set (Audio > TX routing). Tone-remote TX unavailable."); return; }
            if (!String.IsNullOrEmpty(_config.OperatorOutputDevice) && r.PlannedTxOutput == _config.OperatorOutputDevice)
            {
                if (r.TxOutputJack == TxJack.WholeDevice) Log("WARNING " + r.Name + ": TX output is the same device as the operator speaker. Windows sounds, alert audition and recall would be sent to the radio. Use a separate sound card or pick the Side/Rear jack (Audio > TX routing).");
                else Log(r.Name + ": TX shares the onboard output with the operator speaker; radio audio goes only to the " + r.TxOutputJack + " jack. Keep Windows enhancements / speaker fill OFF so stereo sounds stay on the front jack.");
            }
            int outId = FindDevice(r.PlannedTxOutput, false), micId = String.IsNullOrEmpty(r.MicrophoneDevice) ? -1 : FindDevice(r.MicrophoneDevice, true);
            if (outId < 0) { link.TrcError = "TX device missing"; Log(r.Name + ": TX output '" + r.PlannedTxOutput + "' not found or not unique. Re-select it."); return; }
            if (!String.IsNullOrEmpty(r.MicrophoneDevice) && micId < 0) Log(r.Name + ": microphone '" + r.MicrophoneDevice + "' not found; voice TX disabled, tones still work.");
            link.Trc = new TrcEngine();
            string id = r.Id;
            link.Trc.VoiceTap = delegate(short[] s, int n) { _recorder.AddTx(id, s, n); };
            string err = link.Trc.Open(outId, micId, r.TxOutputJack);
            if (err != null) { link.TrcError = err; link.Trc = null; Log(r.Name + ": TRC audio could not start: " + err); return; }
            link.TrcError = ""; Log(r.Name + ": TRC output open on '" + r.PlannedTxOutput + "' (" + link.Trc.OutputLayout + ")" + (micId >= 0 ? ", mic '" + r.MicrophoneDevice + "'" : ", no mic") + ". Line is silent until a TX action.");
        }

        private static int FindDevice(string name, bool input)
        {
            int match = -1;
            try { foreach (AudioDevice d in ReceiveAudio.Devices(input)) if (d.Name == name) { if (match >= 0) return -1; match = d.Id; } } catch (Exception) { }
            return match;
        }

        private void CloseLinks()
        {
            foreach (RadioLink link in _links.Values.ToArray()) { try { link.Dispose(); } catch (Exception) { } }
            _links.Clear();
        }
        private void CloseLink(string id) { RadioLink l; if (_links.TryGetValue(id, out l)) { try { l.Dispose(); } catch (Exception) { } _links.Remove(id); } }

        // While the console transmits in Live, the radio's own SB9600 report says whether it is really on the air. A radio
        // that keys on the high-level guard tone and then drops means the low-level guard tone is not being detected
        // (level too low, AGC off on the TRC board, or Windows audio processing on the TX device): the console says so
        // instead of showing "transmitting" while nothing goes out.
        private void WatchRadioKeyed(RadioLink link)
        {
            RadioProfile r = link.Radio; RadioState s;
            if (_opMode != OpMode.Live || !link.BusOpen || !_tx.Active || !_tx.Targets.Contains(r.Id) || r.Ptt != PttMethod.TrcTones || !_states.TryGetValue(r.Id, out s)) return;
            DateTime now = DateTime.UtcNow;
            if (s.BusTx) { s.TxSeenThisKey = true; s.TxDropAt = DateTime.MaxValue; if (s.RadioDropped) { s.RadioDropped = false; Log(r.Name + ": radio is transmitting again."); } return; }
            if (s.TxSeenThisKey)
            {
                if (s.TxDropAt == DateTime.MaxValue) s.TxDropAt = now;
                if (!s.RadioDropped && (now - s.TxDropAt).TotalMilliseconds > 400)
                {
                    s.RadioDropped = true;
                    if (!s.DropLogged)
                    {
                        s.DropLogged = true;
                        Log("WARNING " + r.Name + ": the radio STOPPED TRANSMITTING " + ((s.TxDropAt - s.KeyedAt).TotalSeconds).ToString("0.0") + " s after keying, while PTT is still held (SB9600 report). "
                            + "The consolette keys on the loud guard tone, then needs the low-level guard tone to stay keyed. Check: Audio > Levels / calibration (HLGT 775 mVrms at the consolette), "
                            + "TRC board S100-4 OFF (AGC on), and Windows audio enhancements OFF on the TX device. HARDWARE_SETUP.md section 10.");
                    }
                }
            }
            else if (!s.NoKeyLogged && (now - s.KeyedAt).TotalMilliseconds > 1500)
            {
                s.NoKeyLogged = true;
                Log("WARNING " + r.Name + ": no SB9600 transmit report 1.5 s after keying. The radio may not have keyed (check the TX output device and levels), or the radio does not report it.");
            }
        }
        private void MonitorLinks()
        {
            foreach (RadioLink link in _links.Values)
            {
                WatchRadioKeyed(link);
                if (link.Trc != null && link.Trc.Faulted && !link.FaultLogged)
                {
                    link.FaultLogged = true; Log(link.Radio.Name + ": TRC audio stopped: " + link.Trc.FaultText);
                    if (_tx.Targets.Contains(link.Radio.Id)) StopTx("Audio device error");
                }
                AudioPane pane;
                if (_audioPanes.TryGetValue(link.Radio.Id, out pane)) pane.Duck = link.Radio.MuteRxOnTx && _tx.Targets.Contains(link.Radio.Id);
            }
            // Live alerts / pages end when the audio engine finishes the program (tones + audio + tail).
            if (_tx.Active && _tx.Reason != "PTT" && _opMode == OpMode.Live && (DateTime.UtcNow - _tx.Started).TotalMilliseconds > 400)
            {
                bool running = _tx.Targets.Any(id => { RadioLink l; return _links.TryGetValue(id, out l) && l.Trc != null && l.Trc.Active; });
                if (!running) { StopTx("Sent"); if (_queueRunning) NextPage(); }
            }
        }

        private TrcParams Params(RadioProfile r, int functionHz)
        {
            return new TrcParams { Tone = r.Tone, FunctionHz = functionHz, ToneScale = r.ToneLevel / 100.0, MicGain = r.MicLevel / 50.0, Notch = r.MicNotch, UseMic = true, MaxMs = _config.PttTimeoutSeconds * 1000, ProgramGain = 1.0 };
        }

        // =====================================================================
        // Transmit
        // =====================================================================
        private void StartPtt(string scope)
        {
            if (_tx.Active) return;
            if (!TxAllowed("PTT")) return;
            _queueRunning = false;
            IEnumerable<RadioProfile> targets = scope == "ALL" ? _config.Radios : scope == "GROUP" ? _config.Radios.Where(r => _states[r.Id].Group) : new RadioProfile[] { _selected };
            List<RadioProfile> list = targets.ToList();
            if (list.Count == 0) { Log("No radios have GROUP ticked."); return; }
            list = list.Where(r => !SeekBlocked(r, "PTT") && !RxOnlyBlocked(r, "PTT")).ToList();
            if (list.Count == 0) return;
            List<string> keyed = KeyRadios(list, null, "PTT");
            if (keyed.Count == 0) return;
            StartTxState(keyed, "PTT", 0);
        }

        private List<string> KeyRadios(List<RadioProfile> radios, short[] program, string reason)
        {
            var ok = new List<string>();
            foreach (RadioProfile r in radios)
            {
                RadioLink link = Link(r);
                if (_opMode == OpMode.Simulation)
                {
                    if (r.Ptt == PttMethod.Sb9600Button && link != null && link.BusOpen) { int code = SbDecoder.ButtonCode("PTT", r.Bus.HeadButtons); if (code >= 0 && link.Bus.Button(code, 1)) link.BusKeyed = true; }
                    ok.Add(r.Id); continue;
                }
                if (link == null) continue;
                if (!ChannelCanTransmit(r)) continue;
                ChannelProfile ch = r.Channels[r.SelectedChannel];
                bool keyed = false;
                if (r.Ptt == PttMethod.TrcTones)
                {
                    if (!link.TrcOpen) { Log(r.Name + ": cannot key - TRC output not open (" + (link.TrcError.Length > 0 ? link.TrcError : "set TX output") + ")."); continue; }
                    TrcParams p = Params(r, ch.FunctionHz); p.Program = program; p.UseMic = program == null;
                    keyed = link.Trc.BeginKeyed(p);
                    if (keyed) ExpectReply(r, reason + " (TRC)", "TX", 1500);
                    if (!keyed) Log(r.Name + ": TRC output busy (tone sequence in progress).");
                }
                else
                {
                    int code = SbDecoder.ButtonCode("PTT", r.Bus.HeadButtons);
                    if (!link.BusOpen || !link.Bus.TransmitAllowed || code < 0) { Log(r.Name + ": cannot key over SB9600 - bus " + (link.BusOpen ? (link.Bus.TransmitAllowed ? "has no head button named PTT" : "is listen-only") : "not connected") + "."); continue; }
                    keyed = link.Bus.Button(code, 1); link.BusKeyed = keyed;
                    if (keyed && link.TrcOpen) { TrcParams p = Params(r, ch.FunctionHz); p.Program = program; p.UseMic = program == null; link.Trc.BeginVoiceOnly(p); }
                    else if (keyed && program != null) Log(r.Name + ": keyed over SB9600 but no TX audio output is open; the alert audio cannot be sent.");
                }
                if (keyed) { link.Keyed = true; link.LastJob = reason; ok.Add(r.Id); _recorder.BeginTx(r.Id, r.Name); }
            }
            return ok;
        }

        private void StartTxState(IEnumerable<string> ids, string reason, int durationMs)
        {
            try
            {
                _tx.Start(ids, reason, DateTime.UtcNow, _config.PttTimeoutSeconds, durationMs);
                Trace("TX BEGIN " + reason + " targets=" + String.Join(",", _tx.Targets.ToArray()));
                Log((_opMode == OpMode.Live ? "ON AIR " : "TEST TX ") + reason + ": " + String.Join(", ", _config.Radios.Where(r => _tx.Targets.Contains(r.Id)).Select(r => r.Name + " / " + r.Channels[r.SelectedChannel].Name).ToArray()));
                foreach (string id in _tx.Targets) { RadioState st; if (_states.TryGetValue(id, out st)) { st.TxSeenThisKey = false; st.RadioDropped = false; st.DropLogged = false; st.NoKeyLogged = false; st.TxDropAt = DateTime.MaxValue; st.KeyedAt = DateTime.UtcNow; } }
                RefreshCards(); RefreshIndicators();
            }
            catch (Exception ex) { Log(ex.Message); }
        }

        private void ReleaseRadio(string id, bool abort)
        {
            RadioLink link; if (!_links.TryGetValue(id, out link)) return;
            if (link.Trc != null) { if (abort) link.Trc.Abort(); else link.Trc.Release(); }
            if (link.BusKeyed)
            {
                int code = SbDecoder.ButtonCode("PTT", link.Radio.Bus.HeadButtons);
                if (code >= 0) link.Bus.Button(code, 0);
                link.BusKeyed = false;
            }
            link.Keyed = false; _recorder.EndTx(id);
        }

        private void StopTx(string reason)
        {
            if (!_tx.Active) return;
            foreach (string id in _tx.Targets.ToArray()) ReleaseRadio(id, false);
            Trace("TX END " + reason); _tx.Stop(); Log((_opMode == OpMode.Live ? "TX ended: " : "SIM TX ended: ") + reason); RefreshCards(); RefreshIndicators();
        }

        private void StopAll(string reason)
        {
            _queueRunning = false; _heldPttKey = Keys.None; _macro.Clear(); _seeks.Clear();
            if (_tx.Active) { foreach (string id in _tx.Targets.ToArray()) ReleaseRadio(id, true); Trace("TX END " + reason); _tx.Stop(); }
            foreach (RadioLink link in _links.Values)
            {
                if (link.Trc != null && link.Trc.Active) link.Trc.Abort();
                if (link.BusKeyed) ReleaseRadio(link.Radio.Id, true);
            }
            StopPreview();
            foreach (RadioState state in _states.Values) state.Beacon = false;
            if (_lcd != null) { RefreshDisplay(); RefreshCards(); }
            if (_queueList != null) RefreshQueue();
            if (!_closing) Log(reason + ": transmit, tones, queue, macros and playback stopped. (RX monitoring keeps running.)");
        }

        // =====================================================================
        // Closed-loop channel selection over SB9600 (zones + MODE keys, verified by the radio display)
        // =====================================================================
        // A channel change in progress. After every command the seek waits for a FRESH display message from the radio
        // (AwaitSeq) before deciding the next step, so it never acts on the display from before its last key press.
        // That stale-readback overshoot is what made HOME and the drop-downs step back and forth through zones.
        private sealed class Seek
        {
            public string RadioId = ""; public int Target, Presses, ZonePresses, Unknown, LastZone, GotoTries, SentMode = -1, AwaitSeq = -1;
            public DateTime Next, Started, AwaitUntil, ModeWait;
        }
        private readonly Dictionary<string, Seek> _seeks = new Dictionary<string, Seek>();

        // awaitFirst: a key that already moves the radio (HOME) was just pressed; read its result before doing anything.
        private void StartSeek(RadioProfile r, int target, int lastZone, bool awaitFirst)
        {
            if (!BusUsable(r))
            {
                // TRC can only select modes 1-8 on the Standard tone table; the bench TRC board runs No Mode Select, so there is no
                // tone fallback for channel changes. Say so instead of failing silently.
                Log(r.Name + ": channel not changed on the radio - " + (BusUnusableReason(r).Length > 0 ? BusUnusableReason(r) : "SB9600 unavailable") + ". Change it on the W7; the console will follow the display.");
                return;
            }
            var seek = new Seek { RadioId = r.Id, Target = target, LastZone = lastZone, Next = DateTime.UtcNow, Started = DateTime.UtcNow };
            if (awaitFirst) Await(seek, _states[r.Id], 1500);
            _seeks[r.Id] = seek;
            ChannelProfile t = r.Channels[target];
            Log(r.Name + ": selecting Z" + t.ZoneNumber + "/" + t.ZonePosition + " " + t.Name + " over SB9600...");
        }
        // A channel change that did not complete: say so, and show the channel the radio display actually reports.
        private void FailSeek(RadioProfile r, string message)
        {
            _seeks.Remove(r.Id); _lastCheck = "channel change NOT CONFIRMED";
            Log(message + " The console now shows the channel the radio reports.");
            RevertToRadio(r);
        }
        private void RevertToRadio(RadioProfile r)
        {
            RadioState s; if (!_states.TryGetValue(r.Id, out s)) return;
            int zone = r.Channels[Math.Max(0, Math.Min(r.SelectedChannel, r.Channels.Count - 1))].ZoneNumber;
            int at = LocateDisplayed(r, s.BusDisplay, zone); if (at < 0) at = LocateDisplayed(r, s.BusDisplay2, zone);
            int byMode = ModeToChannel(r, s.BusChannel); if (at >= 0 && !UniqueLabel(r, at) && SameLabel(r, byMode, at)) at = byMode;
            if (at >= 0) r.SelectedChannel = at;
            if (r == _selected) RefreshSelected(); else RefreshCards();
        }
        private static void Await(Seek seek, RadioState s, int ms) { seek.AwaitSeq = s.DisplaySeq; seek.AwaitUntil = DateTime.UtcNow.AddMilliseconds(ms); }

        // Mode number to send for a direct select: learned for that channel, else the learned offset, else inferred from the
        // radio's last mode report, else ChannelBase. Every guess is checked against the display before it is trusted.
        private static int GuessMode(RadioProfile r, RadioState s, int current, int target)
        {
            int m;
            if (r.Channels[target].BusMode >= 0) m = r.Channels[target].BusMode;
            else if (r.Bus.GotoOffsetKnown) m = target + r.Bus.GotoOffset;
            else if (s.BusChannel >= 0 && current >= 0) m = target + (s.BusChannel - current);
            else m = target + r.Bus.ChannelBase;
            return m >= 0 && m <= 255 ? m : -1;
        }
        private void SendGoto(RadioLink link, Seek seek, RadioState s, int mode)
        {
            s.BusChannel = -1;   // so a mode report seen afterwards is the radio's answer to this command
            link.Bus.Send(0x01, 0x00, (byte)mode, 0x1F); link.Bus.Send(0x01, 0x01, (byte)mode, 0x60);
            seek.SentMode = mode; seek.GotoTries++; seek.ModeWait = DateTime.MinValue; Await(seek, s, 2500);
        }

        // Index of the channel the radio display shows; when a label exists in several zones, prefer preferZone.
        internal static int LocateDisplayed(RadioProfile r, string display, int preferZone)
        {
            string text = (display ?? "").Trim(); if (text.Length == 0) return -1;
            var hits = new List<int>();
            for (int i = 0; i < r.Channels.Count; i++)
            {
                ChannelProfile c = r.Channels[i]; string label = String.IsNullOrWhiteSpace(c.RadioText) ? c.Name : c.RadioText;
                if (String.Equals(System.Text.RegularExpressions.Regex.Replace(label.Trim(), @"\s+", " "), System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " "), StringComparison.OrdinalIgnoreCase)) hits.Add(i);
            }
            if (hits.Count == 0) return DisplayText.FindChannel(r, text);
            foreach (int i in hits) if (r.Channels[i].ZoneNumber == preferZone) return i;
            return hits[0];
        }

        // Presses a zone key; returns the zone the radio should now be in, or -1 if no zone keys are configured.
        private int PressZone(RadioProfile r, int fromZone, int toZone)
        {
            RadioLink link = Link(r); if (link == null) return -1;
            int direct = SbDecoder.ButtonCode("ZONE " + toZone, r.Bus.HeadButtons);
            if (direct >= 0) { link.Bus.Button(direct, 2); return toZone; }
            int up = SbDecoder.ButtonCode("ZONE UP", r.Bus.HeadButtons), dn = SbDecoder.ButtonCode("ZONE DN", r.Bus.HeadButtons);
            int zones = r.Channels.Max(c => c.ZoneNumber); int d = toZone - fromZone;
            if (d > zones / 2) d -= zones; if (d < -zones / 2) d += zones;
            bool goUp = d > 0 ? up >= 0 : dn < 0;
            int code = goUp ? up : dn;
            if (code < 0) return -1;
            link.Bus.Button(code, 2);
            return goUp ? (fromZone % zones) + 1 : ((fromZone + zones - 2) % zones) + 1;
        }
        private static bool UniqueLabel(RadioProfile r, int index)
        {
            string label = (r.Channels[index].RadioText ?? "").Trim(); if (label.Length == 0) return true;
            return r.Channels.Count(c => String.Equals((c.RadioText ?? "").Trim(), label, StringComparison.OrdinalIgnoreCase)) == 1;
        }

        private void ProcessSeeks()
        {
            if (_seeks.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            foreach (Seek seek in _seeks.Values.ToArray())
            {
                RadioProfile r = RadioById(seek.RadioId); RadioState s;
                if (r == null || !_states.TryGetValue(r.Id, out s) || seek.Target >= r.Channels.Count) { _seeks.Remove(seek.RadioId); continue; }
                ChannelProfile target = r.Channels[seek.Target];
                if ((now - seek.Started).TotalSeconds > 30 || seek.Presses > 40 || seek.ZonePresses > 12) { FailSeek(r, r.Name + ": could not reach " + target.Name + " over SB9600 (check MODE/ZONE head buttons and the display text)."); continue; }
                if (_tx.Targets.Contains(r.Id)) { seek.Next = now.AddMilliseconds(500); continue; }
                RadioLink link = Link(r); if (link == null || !link.BusOpen) { _seeks.Remove(r.Id); continue; }
                // Wait for the radio's answer to the last command (or give up waiting after AwaitUntil).
                bool noAnswer = false;
                if (seek.AwaitSeq >= 0)
                {
                    if (s.DisplaySeq == seek.AwaitSeq && now < seek.AwaitUntil) continue;
                    noAnswer = s.DisplaySeq == seek.AwaitSeq; seek.AwaitSeq = -1;
                }
                else if (now < seek.Next) continue;
                // Repeated labels (FIRE MA is Z1/3 and Z3/1) resolve to the zone the radio was last known to be in. After a direct
                // select they resolve to the commanded zone only if the radio answered AND reported the commanded mode number:
                // a radio that ignored the command may still re-send "FIRE MA" from the other zone.
                bool commanded = seek.SentMode >= 0 && !noAnswer && (UniqueLabel(r, seek.Target) || s.BusChannel == seek.SentMode);
                if (seek.SentMode >= 0 && !noAnswer && !commanded && s.BusChannel < 0 && LocateDisplayed(r, s.BusDisplay, target.ZoneNumber) == seek.Target)
                {
                    // The display shows the target's repeated label; its mode report follows ~600 ms later. Wait for it.
                    if (seek.ModeWait == DateTime.MinValue) seek.ModeWait = now.AddMilliseconds(1500);
                    if (now < seek.ModeWait) { seek.Next = now.AddMilliseconds(100); continue; }
                }
                int pz = commanded ? target.ZoneNumber : seek.LastZone > 0 ? seek.LastZone : target.ZoneNumber;
                int cur = LocateDisplayed(r, s.BusDisplay, pz); if (cur < 0) cur = LocateDisplayed(r, s.BusDisplay2, pz);
                if (cur >= 0 && !UniqueLabel(r, cur)) { int byMode = ModeToChannel(r, s.BusChannel); if (SameLabel(r, byMode, cur)) cur = byMode; }
                if (cur >= 0 && UniqueLabel(r, cur)) seek.LastZone = r.Channels[cur].ZoneNumber;
                if (cur == seek.Target)
                {
                    if (seek.SentMode >= 0)
                    {
                        target.BusMode = seek.SentMode;
                        if (!r.Bus.GotoOffsetKnown || r.Bus.GotoOffset != seek.SentMode - seek.Target) { r.Bus.GotoOffset = seek.SentMode - seek.Target; r.Bus.GotoOffsetKnown = true; Log(r.Name + ": direct channel select confirmed by the radio (mode number = channel + " + r.Bus.GotoOffset + ")."); }
                    }
                    _seeks.Remove(r.Id); r.SelectedChannel = seek.Target; seek.LastZone = target.ZoneNumber;
                    _lastCheck = (target.RadioText ?? target.Name).Trim() + " CONFIRMED";
                    Log(r.Name + ": radio is on " + target.Name + " (" + target.RadioText + ")" + (seek.Presses + seek.ZonePresses > 0 ? " after " + (seek.Presses + seek.ZonePresses) + " key presses." : "."));
                    if (r == _selected) RefreshSelected(); else RefreshCards(); continue;
                }
                // A direct select that did not land on the target: learn from where it did land, retry once, else use keys.
                if (seek.SentMode >= 0)
                {
                    int sent = seek.SentMode; seek.SentMode = -1;
                    if (noAnswer || cur < 0)
                    {
                        // The bench XTL ignores this command (bench log 4). Remember it so later changes go straight to the keys.
                        s.DirectGotoFailed = true; r.Bus.DirectChannelGoto = false; Save();
                        Log(r.Name + ": the radio ignores the direct channel-select command, so DirectChannelGoto is now off for this radio; channels change with the ZONE / MODE keys (each step confirmed from the display).");
                    }
                    else if (seek.GotoTries < 2) { int retry = seek.Target + (sent - cur); if (retry != sent && retry >= 0 && retry <= 255) { SendGoto(link, seek, s, retry); continue; } s.DirectGotoFailed = true; }
                    else { s.DirectGotoFailed = true; Log(r.Name + ": direct channel select numbering does not match the codeplug order; using ZONE / MODE keys instead."); }
                }
                if (r.Bus.DirectChannelGoto && !s.DirectGotoFailed && seek.GotoTries == 0 && cur >= 0)
                {
                    int guess = GuessMode(r, s, cur, seek.Target);
                    if (guess >= 0) { SendGoto(link, seek, s, guess); continue; }
                }
                int mode = SbDecoder.ButtonCode("MODE UP", r.Bus.HeadButtons), modeDn = SbDecoder.ButtonCode("MODE DN", r.Bus.HeadButtons);
                if (cur < 0)
                {
                    // Display unknown (no SBEP text yet): nudge the radio so it re-sends its display.
                    seek.Unknown++; if (seek.Unknown > 4) { FailSeek(r, r.Name + ": no radio display text over SB9600, so the channel cannot be confirmed. Turn on ResetOnConnect or press a W7 key once."); continue; }
                    if (mode >= 0) link.Bus.Button(mode, 2); seek.Presses++; Await(seek, s, 2000); continue;
                }
                ChannelProfile here = r.Channels[cur];
                if (here.ZoneNumber != target.ZoneNumber)
                {
                    int expected = PressZone(r, here.ZoneNumber, target.ZoneNumber);
                    if (expected < 0) { FailSeek(r, r.Name + ": " + target.Name + " is in zone " + target.ZoneNumber + ". LEARN your W7 zone buttons and name them ZONE UP / ZONE DN or ZONE 1.." + r.Channels.Max(c => c.ZoneNumber) + "."); continue; }
                    seek.LastZone = expected; seek.ZonePresses++; Await(seek, s, 2000); continue;
                }
                int size = r.Channels.Count(c => c.ZoneNumber == target.ZoneNumber), d = target.ZonePosition - here.ZonePosition;
                if (d > size / 2) d -= size; if (d < -size / 2) d += size;
                int code = d > 0 ? mode : modeDn;
                if (code < 0) { FailSeek(r, r.Name + ": no head buttons named MODE UP / MODE DN."); continue; }
                link.Bus.Button(code, 2); seek.Presses++; Await(seek, s, 2000);
            }
        }

        internal void EmergencyStop(string reason) { try { StopAll(reason); } catch (Exception) { } }

        // Guard + function tone only (mode select, monitor, secure, user functions).
        private bool SendFunctionTone(RadioProfile r, int hz, string label)
        {
            if (_opMode == OpMode.Simulation) { Trace("TRC FUNCTION " + label + " / guard " + r.Tone.GuardHz + " Hz + " + hz + " Hz"); Log("SIM tone function " + label + ": " + hz + " Hz (no audio)"); return true; }
            if (_tx.Targets.Contains(r.Id)) { Log("Release TX before sending a function tone."); return false; }
            RadioLink link = Link(r);
            if (link == null || !link.TrcOpen) { Log(r.Name + ": TRC output not open; " + label + " not sent."); return false; }
            if (!link.Trc.SendFunction(Params(r, hz))) { Log(r.Name + ": TRC output busy; " + label + " not sent."); return false; }
            link.LastJob = label; Trace("TRC FUNCTION " + label + " / " + r.Tone.GuardHz + " Hz " + r.Tone.GuardMs + " ms + " + hz + " Hz " + r.Tone.FunctionMs + " ms");
            Log(r.Name + ": sent guard + " + hz + " Hz (" + label + ").");
            return true;
        }

        private void ToneFunction(string key)
        {
            ToneProfile t = _selected.Tone; int hz;
            if (key == "A") hz = t.FunctionAHz; else if (key == "B") hz = t.FunctionBHz; else if (key == "C") hz = t.FunctionCHz;
            else if (key == "MON") hz = t.MonitorHz; else if (key == "SECURE") hz = t.SecureCodedHz; else if (key == "CLEAR") hz = t.SecureClearHz;
            else if (!Int32.TryParse(key, out hz) || hz < 20 || hz > 5000) { Log("Unknown tone function '" + key + "'. Use A, B, C, MON, SECURE, CLEAR or a frequency in Hz."); return; }
            if (!TxAllowed("TRC FUNCTION " + key)) return;
            SendFunctionTone(_selected, hz, "FUNC " + key);
        }

        // =====================================================================
        // Channels
        // =====================================================================
        private void StepChannel(int delta)
        {
            if (_opMode != OpMode.Simulation && _selected.ChannelSelect == ChannelMethod.Sb9600ModeKeys)
            {
                RadioLink link = Link(_selected);
                if (BusReady(link, "MODE " + (delta > 0 ? "UP" : "DN")))
                {
                    int code = SbDecoder.ButtonCode(delta > 0 ? "MODE UP" : "MODE DN", _selected.Bus.HeadButtons);
                    if (code >= 0) { link.Bus.Button(code, 2); Log(_selected.Name + ": mode " + (delta > 0 ? "up" : "down") + " sent over SB9600."); }
                    else Log("No head control named MODE UP / MODE DN.");
                }
                return;
            }
            ChangeChannel((_selected.SelectedChannel + delta + _selected.Channels.Count) % _selected.Channels.Count);
        }

        private void ChangeChannel(int index) { ChangeChannel(index, false); }
        // viaHome: HOME was asked for. On a zoned SB9600 radio one press of the radio's own HOME key gets there; the seek then
        // only confirms it from the display (and corrects it if the radio's home is a different channel).
        private void ChangeChannel(int index, bool viaHome)
        {
            if (index < 0 || index >= _selected.Channels.Count) return;
            if (_tx.Active && _tx.Targets.Contains(_selected.Id)) { Log("Release TX before changing channel."); RefreshSelected(); return; }
            RadioState state = _states[_selected.Id]; state.MenuOpen = false; state.DisplayMessage = ""; state.RxChannel = -1;
            int previous = _selected.SelectedChannel; _viewZone = 0;
            _selected.SelectedChannel = index; ChannelProfile ch = _selected.Channels[index];
            Trace("CHANNEL " + _selected.Name + " -> " + ch + " / function " + ch.FunctionHz + " Hz");
            if (_selected.ChannelSelect == ChannelMethod.Manual)
                Log(_selected.Name + " -> " + ch.Name + " (Manual channel select: change the channel on the W7; the console follows the radio display).");
            else if (_selected.ChannelSelect == ChannelMethod.Sb9600ModeKeys)
            {
                int lastZone = previous >= 0 && previous < _selected.Channels.Count ? _selected.Channels[previous].ZoneNumber : 0;
                if (ch.ZonePosition > 0 && _selected.Channels.All(c => c.ZoneNumber > 0))
                {
                    RadioLink hl = Link(_selected); int home = SbDecoder.ButtonCode("HOME", _selected.Bus.HeadButtons);
                    bool pressed = viaHome && home >= 0 && BusUsable(_selected) && hl.Bus.Button(home, 2);
                    StartSeek(_selected, index, lastZone, pressed);
                    // LIVE: the console shows what the radio is on. If the change could not even start (bus listen-only or
                    // not connected), stay on the radio's channel instead of pretending it moved.
                    if (!_seeks.ContainsKey(_selected.Id) && _opMode != OpMode.Simulation) { _selected.SelectedChannel = previous; RevertToRadio(_selected); }
                }
                else StepBusModes(_selected, previous, index);
            }
            else if (_opMode == OpMode.Simulation) Log("SIM channel: " + _selected.Name + " -> " + ch);
            else if (_opMode == OpMode.LiveReceive) Log(_selected.Name + " -> " + ch.Name + ". Mode-select tone not sent (receive-only mode).");
            else if (_armed) SendFunctionTone(_selected, ch.FunctionHz, "MODE SELECT " + ch.Name);
            else Log(_selected.Name + " -> " + ch.Name + ". The mode-select tone is sent with the next PTT (ARM TX is on in Console settings).");
            RefreshSelected();
        }

        // Steps the radio with MODE UP / MODE DN head keys from its reported (or assumed) mode to the target.
        private void StepBusModes(RadioProfile r, int from, int to)
        {
            RadioLink link = Link(r);
            if (!BusReady(link, "mode keys")) return;
            RadioState s = _states[r.Id];
            int current = s.BusChannel >= 0 ? s.BusChannel - r.Bus.ChannelBase : from;
            int delta = to - current;
            int up = SbDecoder.ButtonCode("MODE UP", r.Bus.HeadButtons), dn = SbDecoder.ButtonCode("MODE DN", r.Bus.HeadButtons);
            if (delta == 0) { Log(r.Name + ": radio already on that mode."); return; }
            int code = delta > 0 ? up : dn; if (code < 0) { Log("No head control named MODE UP / MODE DN."); return; }
            int steps = Math.Min(64, Math.Abs(delta));
            for (int i = 0; i < steps; i++)
            {
                _macro.Add(new MacroStep { Action = "sbbtn:" + r.Id + ":" + code + ":2", RadioId = r.Id });
                _macro.Add(new MacroStep { WaitMs = 220 });
            }
            Log(r.Name + ": stepping " + steps + " mode(s) " + (delta > 0 ? "up" : "down") + " over SB9600.");
        }

        private bool BusReady(RadioLink link, string what)
        {
            if (link == null || !link.BusOpen) { Log("SB9600 not connected; " + what + " not sent."); return false; }
            if (!link.Bus.TransmitAllowed) { Log("SB9600 bus is listen-only; " + what + " not sent (tick 'Allow transmit' in the radio's SB9600 settings)."); return false; }
            return true;
        }

        // =====================================================================
        // Functions, head keys, simulation events
        // =====================================================================
        private void Function(string key)
        {
            RadioState state = _states[_selected.Id]; state.LastAction = key;
            if (key == "ACK EMERG") { state.Emergency = false; Log("Emergency acknowledged on " + _selected.Name + "."); RefreshDisplay(); RefreshCards(); return; }
            if (_opMode == OpMode.Simulation)
            {
                if (key == "SCAN") state.Scan = !state.Scan; else if (key == "MON") state.Monitor = !state.Monitor; else if (key == "DIRECT") state.Direct = !state.Direct;
                else if (key == "LOW PWR") state.LowPower = !state.LowPower; else if (key == "SECURE") state.Secure = !state.Secure; else if (key == "BEACON") state.Beacon = !state.Beacon;
                Trace("FUNCTION " + key); Log("SIM " + _selected.Name + ": " + key); RefreshDisplay(); RefreshCards(); return;
            }
            // SB9600 first: press the radio's own key (works in Receive only too; the radio reports the result back). TRC tone as the
            // fail-safe when the bus cannot take it (not connected, listen-only, or failing) - that needs Live mode.
            if (key == "MON")
            {
                if (!TryBusKey("MON", "MONITOR") && TxAllowed("MONITOR tone")) SendFunctionTone(_selected, _selected.Tone.MonitorHz, "MONITOR (TRC fallback)");
            }
            else if (key == "SECURE")
            {
                if (TryBusKey("SECURE", "SECURE")) state.Secure = !state.Secure;
                else if (TxAllowed("SECURE tone")) { bool coded = !state.Secure; if (SendFunctionTone(_selected, coded ? _selected.Tone.SecureCodedHz : _selected.Tone.SecureClearHz, coded ? "SECURE CODED (TRC fallback)" : "SECURE CLEAR (TRC fallback)")) state.Secure = coded; }
            }
            else if (key == "SCAN" || key == "DIRECT" || key == "LOW PWR" || key == "NUIS DEL")
            {
                string name = key == "SCAN" ? "SCAN" : key == "DIRECT" ? "DIR" : key == "NUIS DEL" ? "NUIS DEL" : "PWR";
                string bound = ResolveFunctionKey(name, key);
                if (bound != null) HeadPress(bound, "click");
                else Log(key + ": no W7 key does this yet. LEARN the W7 key that does " + key + " and name it " + name + " (or give a W7 top key the legend " + key + ") - the tone table has no " + key + " tone.");
            }
            else if (key == "BEACON") Log("BEACON is simulation-only in this build.");
            RefreshDisplay(); RefreshCards();
        }

        private bool FunctionUsable(string key)
        {
            if (key == "SCAN") return ResolveFunctionKey("SCAN", key) != null;
            if (key == "DIRECT") return ResolveFunctionKey("DIR", key) != null;
            if (key == "LOW PWR") return ResolveFunctionKey("PWR", key) != null;
            if (key == "NUIS DEL") return ResolveFunctionKey("NUIS DEL", key) != null;
            if (key == "BEACON") return false;   // simulation-only
            return true;                         // MON / SECURE have a TRC fallback; ACK EMERG is console-side
        }
        // Head button for a console function: a head button with that name, else a W7 top key whose legend matches
        // (top key 3 = SCAN on the bench W7). Null when nothing is bound.
        private string ResolveFunctionKey(string name, string key)
        {
            List<HeadButton> hb = _selected.Bus.HeadButtons;
            if (SbDecoder.ButtonCode(name, hb) >= 0) return name;
            BusSettings b = _selected.Bus;
            string[] labels = { b.W7Top1Label, b.W7Top2Label, b.W7Top3Label, b.W7Top4Label }, fallbackLabels = { "EMER", "PHON", "SCAN", "CALL" };
            string[] bindings = { b.W7Top1Binding, b.W7Top2Binding, b.W7Top3Binding, b.W7Top4Binding };
            for (int i = 0; i < 4; i++)
            {
                string legend = String.IsNullOrWhiteSpace(labels[i]) ? fallbackLabels[i] : labels[i].Trim();
                string binding = String.IsNullOrWhiteSpace(bindings[i]) ? "TOP" + (i + 1) : bindings[i].Trim();
                if ((legend.Equals(name, StringComparison.OrdinalIgnoreCase) || legend.Equals(key, StringComparison.OrdinalIgnoreCase)) && SbDecoder.ButtonCode(binding, hb) >= 0) return binding;
            }
            return null;
        }
        // True when the bus can take a command right now: open, transmit allowed, and not failing.
        private bool BusUsable(RadioProfile r) { RadioLink l = Link(r); return l != null && l.BusOpen && l.Bus.TransmitAllowed && !l.Bus.Failing; }
        private string BusUnusableReason(RadioProfile r)
        {
            RadioLink l = Link(r);
            if (l == null || !l.BusOpen) return "SB9600 not connected";
            if (!l.Bus.TransmitAllowed) return "SB9600 is listen-only (AllowTransmit off)";
            if (l.Bus.Failing) return "SB9600 frames are failing (" + l.Bus.TxFailStreak + " in a row; retrying the bus in 10 s)";
            return "";
        }
        // Sends a command as the radio's own head key when SB9600 is primary and usable; returns false (and says why) when the
        // caller should fall back to its TRC tone.
        private bool TryBusKey(string name, string what)
        {
            if (!_selected.Bus.PreferBusCommands) return false;
            if (SbDecoder.ButtonCode(name, _selected.Bus.HeadButtons) < 0) { Log(what + ": no SB9600 key named " + name + " (LEARN it); using the TRC tone."); return false; }
            if (!BusUsable(_selected)) { Log(what + ": " + BusUnusableReason(_selected) + "; using the TRC tone."); return false; }
            HeadPress(name, "click"); Log(_selected.Name + ": " + what + " sent over SB9600."); return true;
        }
        private void HeadPress(string name, string mode)
        {
            RadioLink link = Link(_selected); int code = SbDecoder.ButtonCode(name, _selected.Bus.HeadButtons);
            if (code < 0) { Log("No head control named '" + name + "' on " + _selected.Name + ". Add it in Settings > Console settings > radio tab > Head buttons, or LEARN it in Settings > Engineering > SB9600 monitor / learn."); return; }
            if (!BusReady(link, name)) return;
            if (code == SbDecoder.ButtonCode("PTT", _selected.Bus.HeadButtons) && mode != "up" && (!TxAllowed("head PTT") || !ChannelCanTransmit(_selected))) return;
            if (mode == "down") link.Bus.Button(code, 1);
            else if (mode == "up") link.Bus.Button(code, 0);
            else if (mode == "toggle") link.Bus.Button(code, 2);
            else
            {
                link.Bus.Button(code, 1);
                _macro.Add(new MacroStep { WaitMs = 120 });
                _macro.Add(new MacroStep { Action = "sbbtn:" + _selected.Id + ":" + code + ":0", RadioId = _selected.Id });
            }
            Trace("HEAD " + name + " (0x" + code.ToString("X2") + ") " + mode);
            if (mode != "up" && name != "PTT") ExpectReply(_selected, name, name == "MON" ? "MON" : name == "SCAN" ? "SCAN" : "KEY", name == "MON" || name == "SCAN" ? 1500 : 1000);
        }

        // =====================================================================
        // Command verification: after every command the console waits for the radio's own answer and says whether the
        // radio confirmed it. MON = monitor report, SCAN = scan light, any other key = its key beep / display / light,
        // TRC PTT = the radio's "transmitting" report. LIVE with an open bus only (the simulator always answers).
        // =====================================================================
        private sealed class CmdCheck { public string RadioId = "", What = "", Kind = ""; public int Seq, Beep; public DateTime Deadline; }
        private readonly List<CmdCheck> _checks = new List<CmdCheck>();
        private string _lastCheck = "";
        private static int Signal(RadioState s, string kind)
        {
            return kind == "MON" ? s.MonSeq : kind == "SCAN" ? s.ScanSeq : kind == "TX" ? s.TxOnSeq : s.BeepSeq + s.DisplaySeq + s.IndSeq;
        }
        private void ExpectReply(RadioProfile r, string what, string kind, int ms)
        {
            RadioLink l = Link(r); RadioState s;
            if (_opMode == OpMode.Simulation || l == null || !l.BusOpen || l.Bus.Simulated || !_states.TryGetValue(r.Id, out s)) return;
            _checks.RemoveAll(c => c.RadioId == r.Id && c.What == what);
            _checks.Add(new CmdCheck { RadioId = r.Id, What = what, Kind = kind, Seq = Signal(s, kind), Beep = s.BeepSeq, Deadline = DateTime.UtcNow.AddMilliseconds(ms) });
        }
        private void ProcessChecks()
        {
            if (_checks.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            foreach (CmdCheck c in _checks.ToArray())
            {
                RadioProfile r = RadioById(c.RadioId); RadioState s;
                if (r == null || !_states.TryGetValue(r.Id, out s)) { _checks.Remove(c); continue; }
                if (Signal(s, c.Kind) != c.Seq)
                {
                    _checks.Remove(c); _lastCheck = c.What + " CONFIRMED";
                    Log(r.Name + ": " + c.What + " confirmed by the radio" + (c.Kind == "MON" ? " (monitor " + (s.Monitor ? "ON" : "OFF") + ")" : c.Kind == "SCAN" ? " (scan " + (s.Scan ? "ON" : "OFF") + ")" : c.Kind == "TX" ? " (radio transmitting)" : "") + ".");
                    continue;
                }
                if (now < c.Deadline) continue;
                _checks.Remove(c); _lastCheck = c.What + " NOT CONFIRMED";
                if (c.Kind == "TX") Log("*** " + r.Name + ": the radio did NOT report transmitting after TRC PTT. Check the TRC wiring, levels (HLGT 775 mVrms), S100 switches and the codeplug TRC option. ***");
                else if ((c.Kind == "MON" || c.Kind == "SCAN") && s.BeepSeq != c.Beep) Log(r.Name + ": " + c.What + " - the radio beeped but did not change " + (c.Kind == "MON" ? "monitor" : "scan") + " (it may not be available on this channel).");
                else Log(r.Name + ": " + c.What + " NOT confirmed by the radio. Look for a red line in Settings > Engineering > SB9600 monitor (BUSY / echo), and check AllowTransmit and the key code.");
                RefreshDisplay();
            }
        }

        private void HeadKey(string key)
        {
            RadioState state = _states[_selected.Id]; state.LastAction = key;
            RadioLink link = Link(_selected);
            string mapped = key == "SELECT" ? "SEL" : key;
            bool liveBus = _opMode != OpMode.Simulation && link != null && link.BusOpen;
            if (_opMode != OpMode.Simulation && !liveBus && key != "HOME") { Log("W7 " + key + " not sent: the SB9600 bus is not connected (Settings > Console settings > radio tab > SB9600 / RIB)."); return; }
            if (liveBus)
            {
                if (key == "LEFT" || key == "RIGHT") { StepChannel(key == "LEFT" ? -1 : 1); return; }
                if (SbDecoder.ButtonCode(mapped, _selected.Bus.HeadButtons) >= 0) { HeadPress(mapped, "click"); return; }
                Log("No W7 control named '" + mapped + "'. LEARN it in Settings > Engineering > SB9600 monitor / LEARN.");
                return;
            }
            if (key == "HOME") { ChangeChannel(_selected.HomeChannel, true); return; }
            if (SbDecoder.ButtonCode(mapped, _selected.Bus.HeadButtons) >= 0 && link != null && link.BusOpen) { int c = SbDecoder.ButtonCode(mapped, _selected.Bus.HeadButtons); link.Bus.Button(c, 2); }
            if (key == "MENU") { state.MenuOpen = !state.MenuOpen; state.DisplayMessage = ""; }
            else if (key == "LEFT" || key == "RIGHT")
            {
                if (state.MenuOpen) state.MenuIndex = (state.MenuIndex + (key == "LEFT" ? -1 : 1) + MenuOptions.Length) % MenuOptions.Length;
                else { StepChannel(key == "LEFT" ? -1 : 1); return; }
            }
            else if (key == "SELECT")
            {
                if (state.MenuOpen) { state.MenuOpen = false; Function(MenuOptions[state.MenuIndex]); }
                else state.DisplayMessage = state.Keypad.Length > 0 ? "SELECT " + state.Keypad : "SELECT";
            }
            else if (key.Length == 1 && ConsoleWave.ValidDigits(key))
            {
                state.MenuOpen = false; state.Keypad += key;
                if (state.Keypad.Length > 16) state.Keypad = state.Keypad.Substring(state.Keypad.Length - 16);
                state.DisplayMessage = state.Keypad;
            }
            else if (key == "BACK") { state.MenuOpen = false; if (state.Keypad.Length > 0) state.Keypad = state.Keypad.Substring(0, state.Keypad.Length - 1); state.DisplayMessage = state.Keypad; }
            else if (key == "CALL" || key == "PAGE") { state.MenuOpen = false; state.Keypad = ""; state.DisplayMessage = key + " ID: _"; }
            else if (key == "RSSI") state.DisplayMessage = "SIM RSSI -75 dBm";
            else state.DisplayMessage = "SIM " + key;
            Trace("HEAD " + key + " (simulated)"); RefreshDisplay();
        }

        private void Inject(string kind)
        {
            if (_opMode != OpMode.Simulation) { Log("INJECT events are simulation-only."); return; }
            RadioState state = _states[_selected.Id]; RadioLink link = Link(_selected);
            if (kind == "RX") { state.Rx = true; if (link != null) link.Bus.InjectSimulated(0x01, 0x00, 0x03, 0x1E); }
            if (kind == "CLEAR") { state.Rx = false; if (link != null) link.Bus.InjectSimulated(0x01, 0x00, 0x00, 0x1E); }
            if (kind == "CALLER") { state.Caller = "DEMO-0031"; state.Rx = true; }
            if (kind == "EMERGENCY") { state.Caller = "DEMO-0081"; state.Emergency = true; state.Rx = true; if (link != null) link.Bus.InjectSimulatedIndicator(0x0F, 2); }
            Log("SIM event " + kind + " on " + _selected.Name); RefreshDisplay(); RefreshCards();
        }

        // =====================================================================
        // Alerts, paging, DTMF
        // =====================================================================
        private AlertProfile SelectedAlert { get { return _presets != null && _presets.SelectedItem is AlertProfile ? (AlertProfile)_presets.SelectedItem : _config.Alerts[0]; } }
        private void SendAlert() { SendAlert(SelectedAlert); }
        private void SendAlert(AlertProfile alert)
        {
            if (_tx.Active) { Log("A transmission is already active. Wait or STOP ALL."); return; }
            if (!TxAllowed("ALERT " + alert.Name)) return;
            if (SeekBlocked(_selected, "ALERT " + alert.Name) || RxOnlyBlocked(_selected, "ALERT " + alert.Name)) return;
            _queueRunning = false;
            StartProgram(new List<RadioProfile> { _selected }, alert, "ALERT " + alert.Name);
        }
        // chan: argument -> channel index. Accepts list number (5), zone/position (Z1/5 or 1/5), radio label (CITY FD) or full name.
        internal static int ChannelFromArg(RadioProfile r, string arg)
        {
            string a = (arg ?? "").Trim(); if (a.Length == 0) return -1;
            int n; if (Int32.TryParse(a, out n)) return n >= 1 && n <= r.Channels.Count ? n - 1 : -1;
            string zp = a.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? a.Substring(1) : a; string[] parts = zp.Split('/');
            int z, p;
            if (parts.Length == 2 && Int32.TryParse(parts[0].Trim(), out z) && Int32.TryParse(parts[1].Trim(), out p)) return r.Channels.FindIndex(c => c.ZoneNumber == z && c.ZonePosition == p);
            int i = r.Channels.FindIndex(c => String.Equals((c.RadioText ?? "").Trim(), a, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            return r.Channels.FindIndex(c => String.Equals(c.Name, a, StringComparison.OrdinalIgnoreCase));
        }
        // The XTL sends its mode number (1-based, codeplug order) ~600 ms after the display text. When the display names a
        // unique channel, mode - channel index is the radio's offset; seen twice in a row it is trusted and saved. With the
        // offset known, a repeated label (FIRE MA in Z1 and Z3) is resolved exactly from the mode report.
        private void LearnModeReport(RadioProfile r, RadioState s, int mode)
        {
            if (_seeks.ContainsKey(r.Id)) return;   // the seek learns from its own commands
            int prefer = r.Channels[r.SelectedChannel].ZoneNumber;
            int shown = LocateDisplayed(r, s.BusDisplay, prefer); if (shown < 0) shown = LocateDisplayed(r, s.BusDisplay2, prefer);
            if (shown < 0) return;
            if (UniqueLabel(r, shown))
            {
                int off = mode - shown;
                if (off == s.OffsetCandidate) s.OffsetHits++; else { s.OffsetCandidate = off; s.OffsetHits = 1; }
                if (s.OffsetHits >= 2 && (!r.Bus.GotoOffsetKnown || r.Bus.GotoOffset != off))
                {
                    r.Bus.GotoOffset = off; r.Bus.GotoOffsetKnown = true;
                    Log(r.Name + ": learned the radio's mode numbering from its reports (mode = channel + " + off + ").");
                }
                // Display and mode report agree on a channel other than the operator's: the radio itself was changed
                // (W7 knob, HOME, a key from the W7 window). Scan landings send no mode report, so they never get here.
                if (shown != r.SelectedChannel && r.Bus.SyncChannelFromDisplay && !s.Scan && !_tx.Targets.Contains(r.Id))
                    FollowRadio(r, s, shown, "its display and mode report");
                return;
            }
            int byMode = ModeToChannel(r, mode);
            if (byMode >= 0 && byMode != r.SelectedChannel && SameLabel(r, byMode, shown) && r.Bus.SyncChannelFromDisplay && !s.Scan && !_tx.Targets.Contains(r.Id))
                FollowRadio(r, s, byMode, "its mode report");
        }
        private void FollowRadio(RadioProfile r, RadioState s, int index, string source)
        {
            r.SelectedChannel = index; s.RxChannel = -1;
            Log(r.Name + ": channel changed at the radio - now " + r.Channels[index].Zone + " / " + r.Channels[index].Name + " (from " + source + ").");
            if (r == _selected) RefreshSelected(); else RefreshCards();
        }
        private static int ModeToChannel(RadioProfile r, int mode)
        {
            if (!r.Bus.GotoOffsetKnown || mode < 0) return -1;
            int i = mode - r.Bus.GotoOffset; return i >= 0 && i < r.Channels.Count ? i : -1;
        }
        private static bool SameLabel(RadioProfile r, int a, int b)
        {
            return a >= 0 && b >= 0 && String.Equals((r.Channels[a].RadioText ?? "").Trim(), (r.Channels[b].RadioText ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
        // True when the radio's own display (SB9600 readback) shows the channel the console has selected.
        private bool ChannelConfirmed(RadioProfile r)
        {
            RadioState s; RadioLink link = Link(r);
            if (link == null || !link.BusOpen || !_states.TryGetValue(r.Id, out s) || r.SelectedChannel < 0 || r.SelectedChannel >= r.Channels.Count) return false;
            int zone = r.Channels[r.SelectedChannel].ZoneNumber;
            int at = LocateDisplayed(r, s.BusDisplay, zone); if (at < 0) at = LocateDisplayed(r, s.BusDisplay2, zone);
            if (at != r.SelectedChannel) return false;
            if (UniqueLabel(r, at)) return true;
            int byMode = ModeToChannel(r, s.BusChannel);   // FIRE MA: only the mode report tells the zones apart
            return byMode < 0 || byMode == r.SelectedChannel;
        }
        // Last check before any live keying (PTT, alerts, pages, head PTT): never transmit on a receive-only channel, on a
        // channel without a usable TRC function tone, or on a channel other than the one the radio display shows.
        private bool ChannelCanTransmit(RadioProfile r)
        {
            if (_opMode == OpMode.Simulation) return true;
            if (r.SelectedChannel < 0 || r.SelectedChannel >= r.Channels.Count) return false;
            ChannelProfile ch = r.Channels[r.SelectedChannel];
            if (ch.RxOnly) { Log("NOT KEYED: " + r.Name + " / " + ch.Name + " is RECEIVE ONLY in the codeplug."); return false; }
            if (_seeks.ContainsKey(r.Id)) { Log("NOT KEYED: " + r.Name + " is still changing channel."); return false; }
            if (r.Ptt == PttMethod.TrcTones && (ch.FunctionHz < 20 || ch.FunctionHz > 5000)) { Log("NOT KEYED: " + r.Name + " / " + ch.Name + " has no TRC function tone. Set FunctionHz (1950 for the No Mode Select table)."); return false; }
            RadioLink link = Link(r); RadioState s;
            if (link != null && link.BusOpen && _states.TryGetValue(r.Id, out s))
            {
                int shown = LocateDisplayed(r, s.BusDisplay, ch.ZoneNumber); if (shown < 0) shown = LocateDisplayed(r, s.BusDisplay2, ch.ZoneNumber);
                if (shown >= 0 && shown != r.SelectedChannel && s.Scan && s.RxChannel == shown)
                    Log("Scan: " + r.Name + " is receiving on " + r.Channels[shown].Name + ". The codeplug's scan talkback setting decides whether this transmission goes out on " + r.Channels[shown].Name + " or on " + ch.Name + ".");
                else if (shown >= 0 && shown != r.SelectedChannel) { Log("NOT KEYED: the radio display shows " + r.Channels[shown].Name + " but the console has " + ch.Name + " selected. Select the channel again."); return false; }
                if (shown < 0 && r.Ptt == PttMethod.Sb9600Button && r.Channels.Count > 1) { Log("NOT KEYED: " + r.Name + " channel is not confirmed by the radio display (SB9600 PTT keys whatever channel the radio is on)."); return false; }
            }
            return true;
        }
        private bool SeekBlocked(RadioProfile r, string what)
        {
            if (!_seeks.ContainsKey(r.Id)) return false;
            Log("NOT SENT (" + what + "): " + r.Name + " is still changing channel; wait for 'radio is on ...'.");
            return true;
        }
        // Receive-only channels (codeplug personalities with TX disabled) are never keyed by the console.
        private bool RxOnlyBlocked(RadioProfile r, string what)
        {
            ChannelProfile ch = r.Channels[r.SelectedChannel];
            if (!ch.RxOnly) return false;
            Log("NOT SENT (" + what + "): " + r.Name + " / " + ch.Name + " is RECEIVE ONLY in the codeplug" + (ch.Personality > 0 ? " (personality " + ch.Personality + ")" : "") + ".");
            return true;
        }
        private bool StartProgram(List<RadioProfile> radios, AlertProfile alert, string reason)
        {
            if (_opMode == OpMode.Simulation) { StartTxState(radios.Select(r => r.Id), reason, alert.TotalMs); return true; }
            var ok = new List<string>();
            foreach (RadioProfile r in radios) ok.AddRange(KeyRadios(new List<RadioProfile> { r }, ConsoleWave.Alert(alert, r.AlertLevel), reason));
            if (ok.Count == 0) return false;
            StartTxState(ok, reason, 0); return true;
        }
        private void Enqueue() { Enqueue(SelectedAlert); }
        private void Enqueue(AlertProfile alert)
        {
            if (_queue.Count >= 100) { Log("Paging queue limit: 100 items."); return; }
            _queue.Add(new PageJob(_selected, alert)); RefreshQueue(); Log("Queued " + alert.Name + " for " + _selected.Name + " / " + _selected.Channels[_selected.SelectedChannel].Name);
        }
        private void StartQueue()
        {
            if (_tx.Active || _queue.Count == 0) return;
            if (!TxAllowed("PAGING QUEUE")) return;
            _queueRunning = true; NextPage();
        }
        private void NextPage()
        {
            if (!_queueRunning || _tx.Active) return;
            if (_queue.Count == 0) { _queueRunning = false; RefreshQueue(); Log("Paging queue complete."); return; }
            PageJob job = _queue[0]; _queue.RemoveAt(0);
            RadioProfile target = RadioById(job.RadioId);
            if (target == null) { Log("Queued radio no longer exists; skipped."); NextPage(); return; }
            target.SelectedChannel = Math.Min(job.ChannelIndex, target.Channels.Count - 1);
            if (target == _selected) RefreshSelected();
            if (_seeks.ContainsKey(target.Id)) { _queue.Insert(0, job); _queueRunning = false; Log("Paging queue paused: " + target.Name + " is still changing channel. Press START again."); RefreshQueue(); return; }
            if (RxOnlyBlocked(target, "PAGE " + job.Alert.Name)) { RefreshQueue(); NextPage(); return; }
            if (!StartProgram(new List<RadioProfile> { target }, job.Alert, "PAGE " + job.Alert.Name)) { _queueRunning = false; Log("Paging queue paused: could not key " + target.Name + "."); }
            RefreshQueue();
        }
        private void RefreshQueue()
        {
            if (_queueList == null || _queueList.IsDisposed) return;
            _queueList.Items.Clear(); foreach (PageJob job in _queue) _queueList.Items.Add(job);
            if (_queueState != null) _queueState.Text = (_queueRunning ? "QUEUE RUNNING" : "QUEUE STOPPED") + "  |  pending " + _queue.Count + "  |  each item keeps its own radio, mode and alert";
        }
        private void Dtmf(bool export)
        {
            string digits = (_digits != null ? _digits.Text : "").Trim().ToUpperInvariant();
            if (digits.Length == 0 || !ConsoleWave.ValidDigits(digits)) { Log("Use DTMF digits 0-9, A-D, *, #."); return; }
            SendOrExportDtmf(digits, export);
        }
        private void SendOrExportDtmf(string digits, bool export)
        {
            var alert = new AlertProfile { Name = "DTMF " + digits, Kind = AlertKind.Dtmf, Digits = digits, GapMs = 100 };
            if (export) ExportWave(ConsoleWave.Alert(alert, _selected.AlertLevel), "DTMF_" + digits.Replace("*", "S").Replace("#", "P") + ".wav");
            else SendAlert(alert);
        }
        private void ExportAlert() { ExportWave(ConsoleWave.Alert(SelectedAlert, _selected.AlertLevel), "Dispatch_" + SelectedAlert.Name.Replace(' ', '_') + ".wav"); }
        private void ExportCtcss()
        {
            double hz = _selected.Channels[_selected.SelectedChannel].CtcssHz;
            if (hz <= 0) { Log("Set a CTCSS frequency for the selected channel in Settings > Console settings > radio tab > Channels / modes first."); return; }
            ExportWave(ConsoleWave.Alert(new AlertProfile { Kind = AlertKind.Steady, FrequencyA = hz, DurationMs = 5000 }, 20), "Dispatch_CTCSS_Test.wav");
        }
        private void ExportTrc() { ExportWave(ConsoleWave.ToneSequence(_selected), "Dispatch_TRC_Sequence_Test.wav"); }
        private void ExportWave(short[] samples, string name)
        {
            using (var picker = new SaveFileDialog { Filter = "PCM WAV (*.wav)|*.wav", FileName = name })
                if (picker.ShowDialog(this) == DialogResult.OK) try { ConsoleWave.Write(picker.FileName, samples); Log("WAV exported: " + picker.FileName); } catch (Exception ex) { Log("WAV export failed: " + ex.Message); }
        }
        private void Audition() { PlayPcm(ConsoleWave.Alert(SelectedAlert, _selected.AlertLevel), ConsoleWave.Rate, "alert " + SelectedAlert.Name + " (local only)"); }
        private void StopPreview()
        {
            if (_preview != null) try { _preview.Dispose(); _preview = null; } catch (Exception ex) { Log("Playback cleanup: " + ex.Message); _preview = null; }
        }

        // =====================================================================
        // Mixer, audio panels, routing
        // =====================================================================
        private void ToggleMute(RadioProfile radio)
        {
            radio.Muted = !radio.Muted; AudioPane pane; if (_audioPanes.TryGetValue(radio.Id, out pane)) pane.SetMonitor(radio.RxVolume, radio.Muted);
            RefreshCards(); if (radio == _selected) RefreshDisplay();
        }
        private void SetLevel(string name, int level)
        {
            if (name == "RX MONITOR") { _selected.RxVolume = level; AudioPane pane; if (_audioPanes.TryGetValue(_selected.Id, out pane)) pane.SetMonitor(level, _selected.Muted); }
            else if (name == "MIC / VOICE") _selected.MicLevel = level; else if (name == "ALERT / PAGING") _selected.AlertLevel = level; else _selected.ToneLevel = level;
            _sliderLabels[name].Text = level + "%"; RefreshCards();
        }
        private void OpenAudio() { OpenAudio(_selected, true); }
        // show = false: created hidden for automatic RX monitoring/recording in LIVE (Audio > Receive audio shows it).
        private AudioPane OpenAudio(RadioProfile radio, bool show)
        {
            if (_opMode == OpMode.Simulation) { if (show) Log("Receive audio needs Live or Receive only (Settings > Operating mode)."); return null; }
            Form existing; AudioPane open;
            if (_audioForms.TryGetValue(radio.Id, out existing)) { if (show) { existing.Show(); existing.Activate(); } return _audioPanes.TryGetValue(radio.Id, out open) ? open : null; }
            var form = new Form { Text = radio.Name + " - LIVE receive audio", Size = FitToScreen(new Size(460, 780)), StartPosition = FormStartPosition.CenterParent, AutoScroll = true, AutoScrollMinSize = new Size(420, 730) };   // scrolls on short screens
            var pane = new AudioPane(Log, RadioResources.AudioFile(_data, radio.Id)) { Dock = DockStyle.Fill };
            pane.SetMonitor(radio.RxVolume, radio.Muted);
            pane.RxTap = delegate(short[] s, int n) { if (!_closing) _recorder.AddRx(radio.Id, radio.Name, s, n); };
            pane.PreferencesChanged = delegate(int level, bool muted) { radio.RxVolume = level; radio.Muted = muted; if (_selected == radio && !_refreshing) RefreshSelected(); else RefreshCards(); };
            pane.StatusChanged = delegate(bool running, string state) { Log("Receive audio " + radio.Name + ": " + (running ? state : "STOPPED")); };
            form.Controls.Add(pane); _audioForms.Add(radio.Id, form); _audioPanes.Add(radio.Id, pane);
            form.Owner = this; IntPtr handle = form.Handle;   // a hidden window still needs its handle for the audio timer
            // Closing the window while monitoring only hides it: receive audio and recording carry on.
            form.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (_closing || _audioClosing || e.CloseReason != CloseReason.UserClosing || !pane.Monitoring) return;
                e.Cancel = true; form.Hide(); Log(radio.Name + ": receive audio window hidden; monitoring and recording continue (Audio > Receive audio shows it again).");
            };
            form.FormClosed += delegate { _audioForms.Remove(radio.Id); _audioPanes.Remove(radio.Id); };
            if (show) form.Show();
            return pane;
        }
        private bool _audioClosing;
        private void CloseAudio()
        {
            _audioClosing = true;
            try { foreach (Form form in _audioForms.Values.ToArray()) form.Close(); } finally { _audioClosing = false; }
        }
        // LIVE / Receive only: every radio's receive monitor starts on its saved devices, so RX is heard and recorded
        // without opening a window. A radio with no saved devices is reported once in the journal.
        private void StartReceiveAudio()
        {
            foreach (RadioProfile radio in _config.Radios)
            {
                AudioPane pane = OpenAudio(radio, false); if (pane == null) continue;
                string why = pane.AutoStart();
                if (why.Length > 0) Log(radio.Name + ": receive audio not started - " + why + ". RX is not recorded until it runs.");
            }
        }

        // ---------------- footswitch PTT ----------------
        private Footswitch _foot;
        private string _footSig = "";
        private bool _footHeld, _footProblemLogged;
        private DateTime _footRetry;
        internal bool? FootTest;   // self-test: stands in for the pedal
        private void CloseFootswitch() { if (_foot != null) { _foot.Dispose(); _foot = null; } _footSig = ""; }
        private void ProcessFootswitch()
        {
            FootswitchSettings cfg = _config.Footswitch;
            if (cfg.Source == FootswitchSource.Off && FootTest == null) { if (_foot != null) CloseFootswitch(); if (_footHeld) FootReleased("Footswitch turned off"); return; }
            bool pressed, ok;
            if (FootTest != null) { pressed = FootTest.Value; ok = true; }
            else
            {
                string sig = Footswitch.Signature(cfg);
                if (_foot == null || sig != _footSig || (!_foot.Ready && DateTime.UtcNow >= _footRetry))
                {
                    if (_foot != null) _foot.Dispose();
                    _foot = new Footswitch(cfg); _foot.Open(); _footSig = sig; _footRetry = DateTime.UtcNow.AddSeconds(3);
                }
                ok = _foot.Read(out pressed);
                if (!ok)
                {
                    if (!_footProblemLogged) { _footProblemLogged = true; Log("Footswitch: " + _foot.Problem + ". PTT from the pedal is off until it can be read; retrying."); }
                    if (DateTime.UtcNow >= _footRetry) _footRetry = DateTime.UtcNow.AddSeconds(3);
                }
                else if (_footProblemLogged) { _footProblemLogged = false; Log("Footswitch ready."); }
            }
            if (!ok) pressed = false;   // unreadable = released: a lost pedal can never hold the transmitter keyed
            if (pressed && !_footHeld) { _footHeld = true; if (!_tx.Active) StartPtt("SELECTED"); }
            else if (!pressed && _footHeld) FootReleased(ok ? "Footswitch released" : "Footswitch lost");
        }
        private void FootReleased(string why) { _footHeld = false; if (IsHoldPtt()) StopTx(why); }

        // ---------------- RIB COM port: reopen after a drop ----------------
        private DateTime _nextPortCheck;
        private void CheckBusPorts()
        {
            if (_opMode == OpMode.Simulation || DateTime.UtcNow < _nextPortCheck) return;
            _nextPortCheck = DateTime.UtcNow.AddSeconds(3);
            string[] names; try { names = System.IO.Ports.SerialPort.GetPortNames(); } catch (Exception) { return; }
            foreach (RadioLink link in _links.Values.ToArray())
            {
                RadioProfile r = link.Radio; string port = r.Bus.PortName;
                if (String.IsNullOrEmpty(port) || link.Bus.Simulated) continue;
                bool present = names.Any(n => String.Equals(n, port, StringComparison.OrdinalIgnoreCase));
                if (link.Bus.IsOpen)
                {
                    if (present) continue;
                    // A USB serial adapter was unplugged: the port vanished while open.
                    if (_tx.Targets.Contains(r.Id) && r.Ptt == PttMethod.Sb9600Button) StopTx("SB9600 port lost");
                    try { link.Bus.Close(); } catch (Exception) { }
                    link.PortDown = true; link.BusError = port + " disappeared";
                    Log(r.Name + ": RIB port " + port + " disappeared. SB9600 control is off; reconnecting when it returns.");
                    RefreshSelected(); continue;
                }
                if (!present) continue;
                string err = link.Bus.Open(r.Bus);
                if (err == null)
                {
                    link.BusError = ""; link.PortDown = false;
                    RadioState st; if (_states.TryGetValue(r.Id, out st)) st.Known.Clear();
                    Log(r.Name + ": RIB port " + port + " reopened.");
                    if (r.Bus.SyncOnConnect && r.Bus.AllowTransmit && r == _selected) ScheduleSync(2500);
                    RefreshSelected();
                }
                else if (!link.PortDown) { link.PortDown = true; link.BusError = err; Log(r.Name + ": " + err + " Retrying every 3 s."); }
            }
        }

        // ---------------- recordings older than KeepDays ----------------
        private DateTime _nextPurge;
        private void ProcessPurge()
        {
            if (DateTime.UtcNow < _nextPurge) return;
            _nextPurge = DateTime.UtcNow.AddHours(1);
            int keep = _config.Recorder.KeepDays; string root = RecordingFolder();
            if (keep <= 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int n = 0; string error = "";
                try { n = CallRecorder.Purge(root, keep, DateTime.Now); } catch (Exception ex) { error = ex.Message; }
                if ((n > 0 || error.Length > 0) && !_closing && IsHandleCreated)
                    try { BeginInvoke((Action)delegate { Log(error.Length > 0 ? "Recording clean-up failed: " + error : "Recording clean-up: deleted " + n + " file(s) older than " + keep + " days."); }); } catch (Exception) { }
            });
        }

        private void ConfigureRouting()
        {
            RadioProfile r = _selected;
            using (var dialog = new RoutingDialog(r))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                dialog.Apply(r); Save(); RefreshSelected();
                if (_opMode == OpMode.Live) { StopAll("TX audio routing changed"); RadioLink link = Link(r); if (link != null) { if (link.Trc != null) { link.Trc.Close(); link.Trc = null; } OpenTrc(link); } }
                Log(r.Name + ": TX audio setup saved.");
            }
        }

        private void OpenCalibration()
        {
            using (var dialog = new CalibrationDialog(_selected, Link(_selected), TxAllowed, Log)) dialog.ShowDialog(this);
        }

        // =====================================================================
        // SB9600 traffic -> console state
        // =====================================================================
        internal string DescribeItem(SbItem item, RadioProfile radio)
        {
            switch (item.Kind)
            {
                case SbItem.Kinds.Rx: return "RX< " + SbDecoder.Describe(item.Frame, radio.Bus.HeadButtons).Text;
                case SbItem.Kinds.Tx: return "TX> " + SbDecoder.Describe(item.Frame, radio.Bus.HeadButtons).Text;
                case SbItem.Kinds.Sbep: return "RX< " + SbDecoder.DescribeSbep(item.Raw).Text;
                case SbItem.Kinds.Raw: return "??  " + item.Text + " [" + SbHex.ToHex(item.Raw, 64) + "]";
                case SbItem.Kinds.Info: return "--  " + item.Text;
                default: return "!!  " + item.Text;
            }
        }

        private void ProcessBus()
        {
            foreach (RadioLink link in _links.Values.ToArray())
            {
                List<SbItem> items = link.Bus.Drain(); if (items.Count == 0) continue;
                RadioProfile radio = link.Radio; RadioState s; if (!_states.TryGetValue(radio.Id, out s)) continue;
                bool changed = false;
                foreach (SbItem item in items)
                {
                    string text = DescribeItem(item, radio);
                    if (!(item.Kind == SbItem.Kinds.Rx && item.Frame.Addr == 0x05 && item.Frame.Func == 0x58)) Trace(radio.Name + " " + text);
                    if (_busMonitor != null && !_busMonitor.IsDisposed) _busMonitor.Append(radio, item, text);
                    if (item.Kind == SbItem.Kinds.Info) Log(radio.Name + " bus: " + item.Text);
                    else if (item.Kind == SbItem.Kinds.Error) Log(radio.Name + " bus ERROR: " + item.Text);
                    else if (item.Kind == SbItem.Kinds.Rx) { changed |= ApplyFrame(radio, s, item.Frame); s.LastBusFrame = DateTime.Now; }
                    else if (item.Kind == SbItem.Kinds.Sbep) { changed |= ApplySbep(radio, s, item.Raw); s.LastBusFrame = DateTime.Now; }
                }
                if (changed) { if (radio == _selected) RefreshDisplay(); RefreshCards(); }
            }
        }

        private bool ApplyFrame(RadioProfile radio, RadioState s, SbFrame f)
        {
            SbInfo info = SbDecoder.Describe(f, radio.Bus.HeadButtons); bool changed = true;
            switch (info.Category)
            {
                case "TX": s.BusTx = info.Value == 1; if (s.BusTx) s.TxOnSeq++; break;
                case "MON": s.Monitor = info.Value == 1; s.Known.Add("MON"); s.MonSeq++; break;
                case "SQL": s.BusRx = info.Value == 3; _recorder.SetCarrier(radio.Id, radio.Name, s.BusRx); break;
                case "AUDIO": s.BusAudioOpen = info.Value == 1; if (s.BusAudioOpen) s.BeepSeq++; break;   // every accepted key beeps (bench logs)
                case "MODE":
                    s.BusChannel = info.Value;
                    if (radio.Channels.All(c => c.ZoneNumber > 0)) { LearnModeReport(radio, s, info.Value); break; }   // zoned: display text + mode report
                    int index = info.Value - radio.Bus.ChannelBase;
                    if (index >= 0 && index < radio.Channels.Count && index != radio.SelectedChannel && !_tx.Targets.Contains(radio.Id) && !s.Scan)
                    {
                        radio.SelectedChannel = index; Log(radio.Name + ": radio reports mode " + info.Value + " -> " + radio.Channels[index].Name);
                        if (radio == _selected) RefreshSelected();
                    }
                    break;
                default: changed = false; break;
            }
            foreach (BusRule rule in SbPattern.Match(f, radio.Bus.Rules))
            {
                changed = true;
                switch (rule.Event)
                {
                    case RuleEvent.RxStart: s.BusRx = true; break;
                    case RuleEvent.RxEnd: s.BusRx = false; break;
                    case RuleEvent.TxStart: s.BusTx = true; break;
                    case RuleEvent.TxEnd: s.BusTx = false; break;
                    case RuleEvent.MonitorOn: s.Monitor = true; break;
                    case RuleEvent.MonitorOff: s.Monitor = false; break;
                    case RuleEvent.Emergency: if (!s.Emergency) Log("*** EMERGENCY on " + radio.Name + " (" + rule.Name + ") ***"); s.Emergency = true; break;
                    case RuleEvent.EmergencyClear: s.Emergency = false; break;
                    case RuleEvent.LogOnly: Log(radio.Name + " bus: " + rule.Name); break;
                }
            }
            return changed;
        }

        private bool ApplySbep(RadioProfile radio, RadioState s, byte[] raw)
        {
            SbInfo info = SbDecoder.DescribeSbep(raw);
            if (info.Category == "DISP")
            {
                if (info.Row == 0) s.BusDisplay = Splice(s.BusDisplay, info.Code, info.Str); else s.BusDisplay2 = Splice(s.BusDisplay2, info.Code, info.Str);
                s.DisplaySeq++;
                // The W7 flashes "MON ON" / "MON OFF" when monitor is toggled: a second source for the MON light.
                string shownNow = System.Text.RegularExpressions.Regex.Replace(s.BusDisplay.Trim(), @"\s+", " ").ToUpperInvariant();
                if (shownNow == "MON ON") { s.Monitor = true; s.Known.Add("MON"); s.MonSeq++; } else if (shownNow == "MON OFF") { s.Monitor = false; s.Known.Add("MON"); s.MonSeq++; }
                // The display alone never moves the zone / channel keys: while scanning, the XTL shows whichever channel it
                // is receiving on, and that only belongs in the display at the top. A real change made at the radio comes
                // with a mode report (LearnModeReport), which is what moves the keys.
                Seek running; bool seeking = _seeks.TryGetValue(radio.Id, out running);
                if (!seeking && !_tx.Targets.Contains(radio.Id))
                {
                    int prefer = radio.Channels[radio.SelectedChannel].ZoneNumber;
                    int shown = LocateDisplayed(radio, s.BusDisplay, prefer); if (shown < 0) shown = LocateDisplayed(radio, s.BusDisplay2, prefer);
                    if (shown >= 0 && s.SyncAdopt && shown != radio.SelectedChannel && !s.BusRx)
                    {
                        // Start-up / Sync radio status: the console takes the channel the radio is really on.
                        s.SyncAdopt = false; FollowRadio(radio, s, shown, "its display during sync");
                    }
                    else if (shown >= 0 && s.SyncAdopt) s.SyncAdopt = false;
                    if (shown >= 0)
                    {
                        int rx = shown == radio.SelectedChannel ? -1 : shown;
                        if (rx != s.RxChannel) { s.RxChannel = rx; if (radio == _selected) RefreshDisplay(); }
                        if (s.BusRx || s.Scan || rx >= 0) { s.LastRxChannel = shown; s.LastRxAt = DateTime.Now; }
                    }
                }
                return true;
            }
            if (info.Category == "IND" && info.Codes != null)
            {
                s.IndSeq++;
                for (int i = 0; i < info.Codes.Length; i++)
                {
                    int code = info.Codes[i], state = info.States[i];
                    if (code == 0xFF)
                    {
                        // ALL = off is authoritative (everything dark); ALL = on is the power-up lamp test, so it proves nothing.
                        s.Indicators.Clear();
                        if (state == 0) { s.Scan = false; s.Direct = false; s.LowPower = false; s.Secure = false; MarkMapped(radio.Bus, s); }
                        continue;
                    }
                    s.Indicators[code] = state;
                    if (radio.Bus.UseLedsForRx && (code == 0x0D || code == 0x0E || code == 0x0F)) s.BusRx = state != 0;
                    if (code == 0x10) s.BusTx = state != 0;
                    bool lit = state != 0; BusSettings bs = radio.Bus;
                    if (code == bs.IndicatorScan) { s.Scan = lit; s.Known.Add("SCAN"); s.ScanSeq++; }
                    if (code == bs.IndicatorMonitor) { s.Monitor = lit; s.Known.Add("MON"); }
                    if (code == bs.IndicatorDirect) { s.Direct = lit; s.Known.Add("DIR"); }
                    if (code == bs.IndicatorLowPower) { s.LowPower = lit; s.Known.Add("LPWR"); }
                    if (code == bs.IndicatorSecure) { s.Secure = lit; s.Known.Add("SECURE"); }
                }
                return true;
            }
            return false;
        }
        private static void MarkMapped(BusSettings b, RadioState s)
        {
            if (b.IndicatorScan >= 0) s.Known.Add("SCAN"); if (b.IndicatorDirect >= 0) s.Known.Add("DIR");
            if (b.IndicatorLowPower >= 0) s.Known.Add("LPWR"); if (b.IndicatorSecure >= 0) s.Known.Add("SECURE");
        }
        // Asks the radio to restart (reset frame 00 00 01 08, as RadioConsole2 does at start-up). It comes back in a few
        // seconds and re-sends its display, mode and every light, so the console's indicators are all known again.
        // ---------------- radio status sync (no restart) ----------------
        // The XTL only reports a light when it changes, so after the console connects it cannot know whether scan is on.
        // Pressing SCAN twice makes the radio report the scan light both ways and redraw its display; scan ends where it
        // started. DIR is filled in from the channel (simplex = DIR) until the radio reports it.
        private DateTime _syncDue = DateTime.MaxValue, _syncCheck = DateTime.MaxValue;
        private string _syncRadio = "";
        internal void ScheduleSync(int ms) { _syncDue = DateTime.UtcNow.AddMilliseconds(ms); }
        private void ProcessSync()
        {
            if (DateTime.UtcNow >= _syncDue) { _syncDue = DateTime.MaxValue; SyncRadioStatus(true); }
            if (DateTime.UtcNow >= _syncCheck)
            {
                _syncCheck = DateTime.MaxValue;
                RadioProfile r = RadioById(_syncRadio); RadioState s;
                if (r != null && _states.TryGetValue(r.Id, out s))
                {
                    s.SyncAdopt = false;
                    bool display = s.BusDisplay.Trim().Length > 0;
                    Log(r.Name + ": status synced - " + (display ? "radio display " + MirrorLine(s.BusDisplay) : "NO display text from the radio (check VOL UP 0x53 / VOL DN 0x52 under Head buttons)") + ", "
                        + (s.Known.Contains("SCAN") ? "scan " + (s.Scan ? "ON" : "OFF") : "scan light NOT reported (check IndicatorScan 0x02)") + ", DIR " + (s.Direct ? "ON" : "OFF") + ".");
                }
                RefreshDisplay();
            }
        }
        internal static void InferDirect(RadioProfile r, RadioState s)
        {
            if (r.Bus.IndicatorDirect < 0 || s.Known.Contains("DIR") || r.SelectedChannel < 0 || r.SelectedChannel >= r.Channels.Count) return;
            ChannelProfile ch = r.Channels[r.SelectedChannel];
            if (ch.RxMHz <= 0 || ch.TxMHz <= 0) return;
            s.Direct = Math.Abs(ch.RxMHz - ch.TxMHz) < 0.0005; s.Known.Add("DIR");
        }
        private void SyncRadioStatus(bool automatic)
        {
            RadioProfile r = _selected; RadioState s = _states[r.Id];
            if (_opMode == OpMode.Simulation) { if (!automatic) Log("Sync radio status works in Live or Receive only (the test radio is always in sync)."); return; }
            if (!BusUsable(r)) { if (!automatic) Log("Sync radio status: " + BusUnusableReason(r) + "."); return; }
            if (_tx.Active || _seeks.ContainsKey(r.Id) || _macro.Count > 0) { if (automatic) ScheduleSync(3000); else Log("Sync radio status: wait until transmit or the channel change has finished."); return; }
            InferDirect(r, s);
            // Bench log 4: a VOL press shows "VOL n", then about 1 s later the radio redraws the whole channel name; a SCAN
            // press sends only the scan light. VOL UP + VOL DN leaves the volume where it was.
            string scan = ResolveFunctionKey("SCAN", "SCAN"); int code = scan == null ? -1 : SbDecoder.ButtonCode(scan, r.Bus.HeadButtons);
            int volUp = SbDecoder.ButtonCode("VOL UP", r.Bus.HeadButtons), volDn = SbDecoder.ButtonCode("VOL DN", r.Bus.HeadButtons);
            if (code < 0 && (volUp < 0 || volDn < 0)) { Log(r.Name + ": sync radio status needs VOL UP / VOL DN and SCAN head keys."); RefreshDisplay(); return; }
            s.SyncAdopt = true;
            if (volUp >= 0 && volDn >= 0)
            {
                _macro.Add(new MacroStep { Action = "sbbtn:" + r.Id + ":" + volUp + ":2", RadioId = r.Id }); _macro.Add(new MacroStep { WaitMs = 350 });
                _macro.Add(new MacroStep { Action = "sbbtn:" + r.Id + ":" + volDn + ":2", RadioId = r.Id }); _macro.Add(new MacroStep { WaitMs = 1600 });
            }
            if (code >= 0)
            {
                s.Known.Remove("SCAN");
                for (int i = 0; i < 2; i++) { _macro.Add(new MacroStep { Action = "sbbtn:" + r.Id + ":" + code + ":2", RadioId = r.Id }); _macro.Add(new MacroStep { WaitMs = 900 }); }
            }
            _syncRadio = r.Id; _syncCheck = DateTime.UtcNow.AddSeconds(6);
            Log(r.Name + ": syncing with the radio - VOL UP / VOL DN so it redraws its display (volume ends where it was), SCAN twice so it reports its scan light (scan ends as it was).");
            RefreshDisplay();
        }
        // Label for a recording: the channel the radio display named while receiving (scan), else the operator's channel.
        private string ChannelLabel(string id, string kind, DateTime start)
        {
            RadioProfile r = RadioById(id); RadioState s; if (r == null || r.Channels.Count == 0) return "";
            int i = r.SelectedChannel;
            if (kind == "RX" && _states.TryGetValue(id, out s) && s.LastRxChannel >= 0 && s.LastRxAt >= start.AddSeconds(-3)) i = s.LastRxChannel;
            if (i < 0 || i >= r.Channels.Count) return "";
            ChannelProfile c = r.Channels[i]; string label = (c.RadioText ?? "").Trim();
            return label.Length > 0 && !String.Equals(label, c.Name, StringComparison.OrdinalIgnoreCase) ? label + " (" + c.Name + ")" : c.Name;
        }
        private void ResyncRadio()
        {
            RadioLink link = Link(_selected);
            if (_opMode == OpMode.Simulation) { Log("Resync is for LIVE modes (the simulated radio is always in sync)."); return; }
            if (!BusReady(link, "resync")) return;
            if (_tx.Active) { Log("Release TX before resyncing the radio."); return; }
            if (MessageBox.Show(this, "This restarts " + _selected.Name + " over SB9600 (a few seconds, no transmit).\nIt then re-sends its display and every light.\nTools > Sync radio status is usually enough and does not restart the radio.\n\nRestart the radio now?", "Restart radio", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _states[_selected.Id].Known.Clear(); link.Bus.Send(0x00, 0x00, 0x01, 0x08);
            Log(_selected.Name + ": resync sent; the radio restarts and reports its display and lights.");
            RefreshDisplay();
        }
        private static string Splice(string line, int col, string text)
        {
            col = Math.Max(0, Math.Min(60, col));
            string padded = (line ?? "").PadRight(col + text.Length);
            string result = padded.Substring(0, col) + text + padded.Substring(col + text.Length);
            return result.Length > 64 ? result.Substring(0, 64) : result;
        }

        private void OpenBusMonitor()
        {
            if (_busMonitor != null && !_busMonitor.IsDisposed) { _busMonitor.Show(); _busMonitor.Activate(); return; }
            _busMonitor = new BusMonitorForm(this);
            _busMonitor.Show(this);
        }
        // ---- callbacks used by the bus monitor ----
        internal RadioProfile MonitorSelectedRadio() { return _selected; }
        internal IList<RadioProfile> MonitorRadios() { return _config.Radios; }
        internal string MonitorLineStatus(RadioProfile r) { RadioLink l = Link(r); return l == null ? "not connected" : l.Bus.LineStatus(); }
        internal void MonitorToggleLine(RadioProfile r, string line) { RadioLink l = Link(r); if (l != null) l.Bus.ToggleLine(line); }
        internal void MonitorBusyTest(RadioProfile r) { RadioLink l = Link(r); Log(r.Name + ": " + (l == null || l.Bus.Simulated ? "BUSY test needs a real RIB connection." : l.Bus.BusyLoopbackTest())); }
        internal void MonitorAddHeadButton(RadioProfile r, string name, int code)
        {
            r.Bus.HeadButtons.RemoveAll(b => String.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
            r.Bus.HeadButtons.Add(new HeadButton(name, code, "learned " + DateTime.Now.ToString("yyyy-MM-dd")));
            Save(); RefreshSelected(); Log(r.Name + ": head button " + name + " = 0x" + code.ToString("X2") + " saved.");
        }
        internal void MonitorAddQuickKey(string label, string action) { if (_config.QuickKeys.Count >= 64) { Log("At most 64 quick keys."); return; } _config.QuickKeys.Add(new QuickKey(label, action, "", "")); QuickKeysChanged(); Log("Quick key " + label + " added."); }
        internal void MonitorAddRule(RadioProfile r, BusRule rule) { if (r.Bus.Rules.Count >= 128) return; r.Bus.Rules.Add(rule); Save(); Log(r.Name + ": bus rule added: " + rule); }
        internal void MonitorSend(RadioProfile r, string hex)
        {
            SbFrame frame; string error;
            if (!SbHex.TryFrame(hex, out frame, out error)) { Log("Frame not sent: " + error); return; }
            RadioLink link = Link(r);
            if (!BusReady(link, "frame " + frame)) return;
            if (frame.Addr == 0x05 && frame.Func == 0x57 && frame.P1 == SbDecoder.ButtonCode("PTT", r.Bus.HeadButtons) && frame.P2 != 0 && (!TxAllowed("PTT frame") || !ChannelCanTransmit(r))) return;
            link.Bus.Send(frame); Log(r.Name + ": sent " + frame);
        }
        internal void MonitorLog(string text) { Log(text); }

        // ---- radio display text -> full names ----
        private void LinkDisplayToChannel()
        {
            RadioState s = _states[_selected.Id]; string text = s.BusDisplay.Trim();
            if (text.Length == 0) { Log("No radio display text yet. Turn to the channel on the W7 (or press MODE UP/DN) with the bus connected."); return; }
            if (text.Length > 32) { Log("Radio display text is longer than 32 characters; use ADD TEXT LOOKUP instead."); return; }
            ChannelProfile ch = _selected.Channels[_selected.SelectedChannel];
            foreach (ChannelProfile other in _selected.Channels) if (other != ch && String.Equals((other.RadioText ?? "").Trim(), text, StringComparison.OrdinalIgnoreCase)) other.RadioText = "";
            ch.RadioText = text; Save(); RefreshSelected();
            Log(_selected.Name + ": radio text '" + text + "' now means channel " + (_selected.SelectedChannel + 1) + " '" + ch.Name + "'.");
        }
        private void AddDisplayLookup()
        {
            RadioState s = _states[_selected.Id]; string text = s.BusDisplay.Trim();
            using (var match = new LabelDialog("TEXT AS THE RADIO SHOWS IT", text.Length > 0 ? text : "CITY FD"))
            {
                if (match.ShowDialog(this) != DialogResult.OK || match.ResultText.Trim().Length == 0) return;
                using (var full = new LabelDialog("FULL TEXT TO SHOW", ""))
                {
                    if (full.ShowDialog(this) != DialogResult.OK || full.ResultText.Trim().Length == 0) return;
                    if (_selected.Bus.DisplayLookups.Count >= 256) { Log("At most 256 lookups."); return; }
                    _selected.Bus.DisplayLookups.RemoveAll(l => String.Equals((l.Match ?? "").Trim(), match.ResultText.Trim(), StringComparison.OrdinalIgnoreCase));
                    _selected.Bus.DisplayLookups.Add(new TextLookup(match.ResultText.Trim(), full.ResultText.Trim()));
                    Save(); RefreshDisplay(); Log(_selected.Name + ": display lookup '" + match.ResultText.Trim() + "' -> '" + full.ResultText.Trim() + "'.");
                }
            }
        }

        // =====================================================================
        // Actions & macros (quick keys, softkeys, hotkeys)
        // =====================================================================
        private void RunAction(string action, string source)
        {
            if (String.IsNullOrWhiteSpace(action) || action.Trim().Equals("unassigned", StringComparison.OrdinalIgnoreCase)) { Log(source + ": no action assigned (right-click the key, or Settings > Console settings, to set one)."); return; }
            string[] steps = action.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (steps.Length == 1 && !steps[0].Trim().StartsWith("wait:", StringComparison.OrdinalIgnoreCase)) { Execute(steps[0].Trim(), _selected); return; }
            if (_macro.Count > 300) { Log("Macro queue is full."); return; }
            foreach (string raw in steps)
            {
                string step = raw.Trim(); if (step.Length == 0) continue;
                if (step.StartsWith("wait:", StringComparison.OrdinalIgnoreCase)) { int ms; Int32.TryParse(step.Substring(5).Trim(), out ms); _macro.Add(new MacroStep { WaitMs = Math.Max(0, Math.Min(10000, ms)) }); }
                else _macro.Add(new MacroStep { Action = step, RadioId = _selected.Id });
            }
            Trace("MACRO " + source + ": " + action);
        }

        private void ProcessMacro()
        {
            int guard = 0;
            while (_macro.Count > 0 && DateTime.UtcNow >= _macroDue && guard++ < 20)
            {
                MacroStep step = _macro[0]; _macro.RemoveAt(0);
                if (step.WaitMs > 0) { _macroDue = DateTime.UtcNow.AddMilliseconds(step.WaitMs); return; }
                RadioProfile radio = RadioById(step.RadioId) ?? _selected;
                Execute(step.Action, radio);
            }
        }

        private void Execute(string action, RadioProfile radio)
        {
            int colon = action.IndexOf(':');
            string verb = (colon < 0 ? action : action.Substring(0, colon)).Trim().ToLowerInvariant();
            string arg = colon < 0 ? "" : action.Substring(colon + 1).Trim();
            if (verb == "sbbtn")
            {
                string[] p = arg.Split(':'); int code, value; RadioProfile r = p.Length == 3 ? RadioById(p[0]) : null; RadioLink l = Link(r);
                if (r != null && l != null && Int32.TryParse(p[1], out code) && Int32.TryParse(p[2], out value)) l.Bus.Button(code, value);
                return;
            }
            if (radio != null && radio != _selected && verb != "select") SelectRadio(radio);
            switch (verb)
            {
                case "func": Function(arg.ToUpperInvariant()); break;
                case "trc": ToneFunction(arg.ToUpperInvariant()); break;
                case "chan":
                    if (arg == "+" || arg.Equals("up", StringComparison.OrdinalIgnoreCase)) StepChannel(1);
                    else if (arg == "-" || arg.Equals("down", StringComparison.OrdinalIgnoreCase)) StepChannel(-1);
                    else
                    {
                        int i = ChannelFromArg(_selected, arg);
                        if (i >= 0) ChangeChannel(i); else Log("chan: no channel '" + arg + "' on " + _selected.Name + ". Use a number, Z1/5, the radio label (CITY FD) or the full name.");
                    }
                    break;
                case "alert":
                case "page":
                    {
                        AlertProfile a = _config.Alerts.FirstOrDefault(x => String.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase));
                        if (a == null) { Log(verb + ": no alert named '" + arg + "'."); break; }
                        if (verb == "alert") SendAlert(a); else Enqueue(a);
                    }
                    break;
                case "dtmf": if (arg.Length > 0 && arg.Length <= 32 && ConsoleWave.ValidDigits(arg)) SendOrExportDtmf(arg.ToUpperInvariant(), false); else Log("dtmf: use 0-9 A-D * #"); break;
                case "head":
                    {
                        string[] p = arg.Split(':'); string mode = p.Length > 1 ? p[p.Length - 1].Trim().ToLowerInvariant() : "click";
                        string name = p.Length > 1 && (mode == "down" || mode == "up" || mode == "toggle" || mode == "click") ? arg.Substring(0, arg.LastIndexOf(':')).Trim() : arg;
                        if (!(mode == "down" || mode == "up" || mode == "toggle")) mode = "click";
                        HeadPress(name, mode);
                    }
                    break;
                case "sb": MonitorSend(_selected, arg); break;
                case "select":
                    {
                        RadioProfile r = _config.Radios.FirstOrDefault(x => String.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase));
                        if (r != null) SelectRadio(r); else Log("select: no radio named '" + arg + "'.");
                    }
                    break;
                case "mute": ToggleMute(_selected); break;
                case "stopall": StopAll("Action stopall"); break;
                case "ack": Function("ACK EMERG"); break;
                case "replay": { int n; ReplayLast(Int32.TryParse(arg, out n) ? Math.Max(1, Math.Min(20, n)) : 1); } break;
                case "note": Log("NOTE: " + arg); break;
                case "tab": SelectTab(arg.ToUpperInvariant()); break;
                default: Log("Unknown action '" + action + "'. Settings > Engineering > Action reference lists them."); break;
            }
        }

        private void ShowActionReference()
        {
            ShowText("Action reference",
                "Quick keys, softkeys and hotkeys run ACTIONS. Join several with ';' to make a macro, e.g.\r\n" +
                "    chan:2; wait:400; alert:ATTENTION\r\n\r\n" +
                "func:SCAN | MON | DIRECT | LOW PWR | SECURE | BEACON | ACK EMERG   console function keys\r\n" +
                "trc:A | B | C | MON | SECURE | CLEAR | <Hz>                         guard tone + function tone\r\n" +
                "chan:<number> | <channel name> | + | -                              select a channel / mode\r\n" +
                "alert:<alert name>                                                  key and send an alert\r\n" +
                "page:<alert name>                                                   add an alert to the paging queue\r\n" +
                "dtmf:<digits>                                                       key and send DTMF\r\n" +
                "head:<head button name>[:down|:up|:toggle]                          press a W7 control over SB9600\r\n" +
                "sb:<4 or 5 hex bytes>                                               send a raw SB9600 frame (CRC added)\r\n" +
                "select:<radio name>   mute   stopall   ack   replay:<n>   tab:<TAB NAME>   note:<text>\r\n" +
                "wait:<ms>                                                           pause inside a macro (max 10000)\r\n\r\n" +
                "Head button names come from Settings > Console settings > radio tab > Head buttons (defaults: PTT, VIP1, VIP2, RADIO SEL, RSSI, SPKR,\r\n" +
                "0-9, *, #, MODE UP, MODE DN, VOL UP, VOL DN, SEL, HOME, DIM, TOP1-TOP6). Use the bus monitor's LEARN to add more.\r\n\r\n" +
                "Hotkeys: " + _config.Hotkeys.Ptt + " PTT (hold), " + _config.Hotkeys.GroupPtt + " group PTT, " + _config.Hotkeys.StopAll + " stop all, " + _config.Hotkeys.ChannelUp + "/" + _config.Hotkeys.ChannelDown + " channel, " +
                _config.Hotkeys.PreviousRadio + "/" + _config.Hotkeys.NextRadio + " radio, " + (String.IsNullOrWhiteSpace(_config.Hotkeys.Monitor) ? "" : _config.Hotkeys.Monitor + " monitor, ") + _config.Hotkeys.ReplayLast + " replay last call. Change them in Settings > Console settings > Hotkeys.");
        }
    }
}
