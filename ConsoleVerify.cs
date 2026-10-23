using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Self-test. Build with Verify.cmd. "Verify.exe" runs the model/DSP/protocol checks; "Verify.exe --ui" also drives the
// main window in SIMULATION (no hardware, temporary config folder).
namespace RadioDispatchConsole
{
    internal static class ConsoleVerify
    {
        private static int _count;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); _count++; }
        private static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
        private static object Call(object target, string name, params object[] args)
        {
            foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == args.Length) return m.Invoke(target, args);
            throw new MissingMethodException(name);
        }
        private static double Energy(short[] data, double hz, int startMs, int durationMs)
        {
            double sin = 0, cos = 0; int start = startMs * 48, length = durationMs * 48;
            for (int i = 0; i < length; i++) { double angle = 2 * Math.PI * hz * i / 48000; sin += data[start + i] * Math.Sin(angle); cos += data[start + i] * Math.Cos(angle); }
            return sin * sin + cos * cos;
        }
        private static double RmsDb(short[] d, int startMs, int durMs)
        {
            double sum = 0; int n = durMs * 48; for (int i = 0; i < n; i++) sum += (double)d[startMs * 48 + i] * d[startMs * 48 + i];
            return 20 * Math.Log10(Math.Sqrt(sum / n) / 32768.0);
        }
        private static byte[] H(string hex) { return hex.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray(); }
        private static List<SbItem> FeedAll(byte[] bytes) { var link = new Sb9600Link(); link.Feed(bytes, bytes.Length); return link.Drain(); }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string temp = Path.Combine(Path.GetTempPath(), "DISPATCH_VERIFY_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
                string path = Path.Combine(temp, "console.config.xml");
                // ---- configuration ----
                var config = DispatchConfig.Load(path); config.Validate();
                Check(config.Radios.Count == 1 && config.Alerts.Count == 6, "Default radio/alert profiles missing.");
                RadioProfile xtl = config.Radios[0];
                Check(xtl.Channels.Count == 8 && xtl.Channels[2].FunctionHz == 1350 && xtl.Channels[6].FunctionHz == 1750, "Standard TRC tone table (manual Table 3-1) not applied to modes.");
                Check(xtl.Bus.HeadButtons.Count == 31 && SbDecoder.ButtonCode("HOME", xtl.Bus.HeadButtons) == 0x61 && SbDecoder.ButtonCode("MODE UP", xtl.Bus.HeadButtons) == 0x51, "W-series head-button defaults wrong.");
                Check(xtl.Softkeys.Count == 8 && config.QuickKeys.Count == 10 && config.Layouts.Count == 4, "Default softkeys/quick keys/layouts wrong.");
                var extra = DispatchConfig.NewRadio("Second radio"); config.Radios.Add(extra);
                for (int i = 0; i < 5; i++) { config.Save(path); config = DispatchConfig.Load(path); }
                Check(config.Radios.Count == 2 && config.Radios[1].Name == "Second radio", "Config round-trip failed.");
                Check(config.Radios[0].Softkeys.Count == 8 && config.Radios[0].Bus.HeadButtons.Count == 31 && config.QuickKeys.Count == 10 && config.Layouts.Count == 4, "Lists duplicated on reload (v0.4 softkey bug regression).");
                Check(File.Exists(path + ".bak"), "Backup missing.");
                var detached = config.Copy(); detached.Radios[0].Name = "Changed"; Check(config.Radios[0].Name != "Changed", "Editor must use an isolated copy.");
                detached.Radios[1].Id = detached.Radios[0].Id; bool bad = false; try { detached.Validate(); } catch (InvalidDataException) { bad = true; } Check(bad, "Duplicate radio IDs must be rejected.");
                File.WriteAllText(Path.Combine(temp, "bad.xml"), "<!DOCTYPE DispatchConfig [<!ENTITY bad SYSTEM 'file:///etc/passwd'>]><DispatchConfig>&bad;</DispatchConfig>");
                bad = false; try { DispatchConfig.Load(Path.Combine(temp, "bad.xml")); } catch { bad = true; } Check(bad, "External XML entities must be rejected.");
                var invalid = config.Copy(); invalid.Radios[0].Tone.GuardHz = Double.NaN; bad = false; try { invalid.Validate(); } catch (InvalidDataException) { bad = true; } Check(bad, "NaN must be rejected.");
                invalid = config.Copy(); invalid.Radios[0].Bus.Rules.Add(new BusRule("x", "01 02", RuleEvent.Emergency)); bad = false; try { invalid.Validate(); } catch (InvalidDataException) { bad = true; } Check(bad, "Malformed bus rule must be rejected.");
                string v04 = "<?xml version=\"1.0\"?><DispatchConfig><ConsoleName>OLD</ConsoleName><OperatorName>Op</OperatorName><PttTimeoutSeconds>60</PttTimeoutSeconds><CompactCards>false</CompactCards><Radios><RadioProfile><Id>XTL</Id><Name>Motorola XTL5000</Name><Model>m</Model><RxVolume>50</RxVolume><MicLevel>70</MicLevel><AlertLevel>60</AlertLevel><ToneLevel>60</ToneLevel><SelectedChannel>0</SelectedChannel><Channels><ChannelProfile><Zone>Z</Zone><Name>MODE 1</Name><FunctionHz>1950</FunctionHz><CtcssHz>0</CtcssHz></ChannelProfile></Channels><Softkeys><SoftKeyProfile><Label>MON</Label><Binding>unassigned</Binding></SoftKeyProfile><SoftKeyProfile><Label>MON</Label><Binding>unassigned</Binding></SoftKeyProfile></Softkeys><Tone><GuardHz>2175</GuardHz><GuardMs>120</GuardMs><FunctionMs>40</FunctionMs><GuardDbfs>-12</GuardDbfs><FunctionDbfs>-22</FunctionDbfs><HoldDbfs>-42</HoldDbfs><MonitorHz>2050</MonitorHz><VoiceDelayMs>200</VoiceDelayMs><SingleTone>false</SingleTone><FunctionAHz>1950</FunctionAHz><FunctionBHz>1850</FunctionBHz><FunctionCHz>1750</FunctionCHz></Tone></RadioProfile></Radios><Alerts><AlertProfile><Name>A</Name><Kind>Steady</Kind><FrequencyA>1000</FrequencyA><FrequencyB>1500</FrequencyB><DurationMs>2000</DurationMs><ToneAMs>1000</ToneAMs><ToneBMs>3000</ToneBMs><GapMs>100</GapMs><Digits>1</Digits></AlertProfile></Alerts></DispatchConfig>";
                File.WriteAllText(Path.Combine(temp, "v04.xml"), v04);
                var migrated = DispatchConfig.Load(Path.Combine(temp, "v04.xml"));
                Check(migrated.Radios[0].Softkeys.Count == 1 && migrated.Radios[0].Tone.TailMs == 120 && migrated.Radios[0].Tone.SecureCodedHz == 1150 && migrated.Ui != null && migrated.Radios[0].Bus.HeadButtons.Count == 31, "v0.4 configuration migration failed.");

                // ---- TX controller ----
                DateTime now = DateTime.UtcNow; var tx = new TxController(); tx.Start(new string[] { "XTL", "XTL", "SECOND" }, "PTT", now, 5, 0);
                Check(tx.Targets.Count == 2 && !tx.Expired(now.AddSeconds(4)) && tx.Expired(now.AddSeconds(5)), "PTT timeout or deduplication failed."); tx.Stop(); Check(!tx.Active, "PTT stop failed.");

                // ---- alert waveforms ----
                foreach (AlertProfile alert in config.Alerts)
                {
                    short[] audio = ConsoleWave.Alert(alert, 60); Check(audio.Length == 48 * alert.TotalMs, "Alert duration incorrect.");
                    Check(audio.Any(s => s != 0), "Alert generated silence."); Check(audio[0] == 0 && audio[audio.Length - 1] == 0, "Edge fades missing.");
                    Check(ConsoleWave.Alert(alert, 0).All(s => s == 0), "Alert mute gain incorrect.");
                }
                short[] twoTone = ConsoleWave.Alert(config.Alerts.First(a => a.Kind == AlertKind.TwoTone), 60);
                Check(Energy(twoTone, 600, 100, 500) > 100 * Energy(twoTone, 1000, 100, 500), "Two-tone A frequency incorrect.");
                Check(Energy(twoTone, 1000, 1300, 500) > 100 * Energy(twoTone, 600, 1300, 500), "Two-tone B frequency incorrect.");
                short[] dtmf = ConsoleWave.Alert(new AlertProfile { Kind = AlertKind.Dtmf, Digits = "1" }, 60);
                Check(Energy(dtmf, 697, 10, 100) > 10 * Energy(dtmf, 941, 10, 100) && Energy(dtmf, 1209, 10, 100) > 10 * Energy(dtmf, 1633, 10, 100), "DTMF pair incorrect.");

                // ---- live TRC tone engine, rendered offline ----
                var engine = new TrcEngine();
                short[] prog = ConsoleWave.Alert(new AlertProfile { Kind = AlertKind.Steady, FrequencyA = 1000, DurationMs = 1000 }, 60);
                var p = new TrcParams { Tone = xtl.Tone, FunctionHz = 1950, ToneScale = 0.6, Program = prog, UseMic = false, MaxMs = 60000 };
                short[] keyed = engine.RenderOffline(p, 1, 4000, -1);
                double hlgt = RmsDb(keyed, 10, 100), llgt = RmsDb(keyed, 165, 30);
                Check(Math.Abs(hlgt - (-12 + 20 * Math.Log10(0.6) - 3.01)) < 0.3, "HLGT level wrong: " + hlgt);
                Check(Energy(keyed, 2175, 10, 100) > 1000 * Energy(keyed, 1950, 10, 100), "HLGT frequency/duration wrong.");
                Check(Energy(keyed, 1950, 125, 30) > 100 * Energy(keyed, 2175, 125, 30), "Function tone missing at 120-160 ms.");
                Check(Math.Abs((hlgt - llgt) - 30) < 0.5, "LLGT must be 30 dB below HLGT: " + (hlgt - llgt));
                Check(Energy(keyed, 1000, 205, 10) > 1e4 * (Energy(keyed, 1000, 185, 10) + 1), "Voice/program must start at the voice delay.");
                Check(keyed.Skip(48 * 250).Take(48 * 900).Max(s => Math.Abs((int)s)) / 32768.0 <= 0.6 * Math.Pow(10, -12 / 20.0) + 0.6 * Math.Pow(10, -42 / 20.0) + 0.001, "Voice exceeded the HLGT ceiling.");
                Check(keyed.Length / 48 >= 1320 && keyed.Length / 48 <= 1340 && RmsDb(keyed, 1210, 100) < -45 && RmsDb(keyed, 1210, 100) > -54, "LLGT tail after program wrong (length " + keyed.Length / 48 + " ms).");
                short[] ptt = engine.RenderOffline(new TrcParams { Tone = xtl.Tone, FunctionHz = 1850, ToneScale = 0.6, UseMic = false }, 1, 4000, 500);
                Check(ptt.Length / 48 >= 620 && ptt.Length / 48 <= 640 && ptt.Skip(48 * 128).Take(48 * 30).Any(s => s != 0), "PTT release/tail timing wrong.");
                short[] tap = engine.RenderOffline(new TrcParams { Tone = xtl.Tone, FunctionHz = 1950, ToneScale = 0.6, UseMic = false }, 1, 4000, 50);
                Check(tap.Length / 48 >= 280 && Energy(tap, 1950, 125, 30) > 100 * Energy(tap, 2175, 125, 30), "A quick tap must still complete guard + function tone.");
                short[] func = engine.RenderOffline(new TrcParams { Tone = xtl.Tone, FunctionHz = 2050, ToneScale = 0.6 }, 0, 4000, -1);
                Check(func.Length / 48 >= 180 && func.Length / 48 <= 200 && func.Skip(48 * 170).All(s => Math.Abs((int)s) < 40), "Function-only sequence must end after the function tone.");
                Check(Math.Abs(TrcEngine.Limit(5.0, 0.2)) <= 0.2 && Math.Abs(TrcEngine.Limit(0.05, 0.2) - 0.05) < 1e-9, "Voice limiter wrong.");
                Biquad notch = Biquad.Notch(2175, 3.8, 48000); double maxOut = 0;
                for (int i = 0; i < 48000; i++) { double y = notch.Process(Math.Sin(2 * Math.PI * 2175 * i / 48000.0)); if (i > 24000) maxOut = Math.Max(maxOut, Math.Abs(y)); }
                Check(maxOut < 0.02, "Guard notch must remove 2175 Hz from mic audio.");

                // ---- SB9600 / SBEP ----
                Check(SbCrc.Compute(new byte[] { 0x05, 0x60, 0x01, 0x57 }, 0, 4) == 0x41 && SbCrc.Compute(new byte[] { 0x05, 0x60, 0x00, 0x57 }, 0, 4) == 0x28 && SbCrc.Compute(new byte[] { 0x05, 0x69, 0x01, 0x57 }, 0, 4) == 0x8F && SbCrc.Compute(new byte[] { 0x00, 0x12, 0x05, 0x06 }, 0, 4) == 0x39, "SB9600 CRC vectors failed.");
                Check(SbCrc.Compute(new byte[] { 0x05, 0x04, 0x01, 0x57 }, 0, 4) == 0xF5 && SbCrc.Compute(new byte[] { 0x05, 0x04, 0xFF, 0x57 }, 0, 4) == 0xCC, "Channel-knob CRC vectors failed.");
                var stream = new List<byte> { 0x77, 0x13 };
                stream.AddRange(new SbFrame(0x01, 0x00, 0x01, 0x19).Bytes());
                stream.AddRange(new SbFrame(0x01, 0x00, 0x03, 0x1E).Bytes());
                stream.AddRange(new SbFrame(0x00, 0x12, 0x05, 0x06).Bytes());
                stream.Add(0x50); stream.AddRange(Sbep.DisplayMessage(0, 0, "FIRE DISP 1   "));
                stream.Add(0x50); stream.AddRange(new SbFrame(0x01, 0x00, 0x04, 0x1F).Bytes());
                stream.AddRange(new SbFrame(0x00, 0x12, 0x05, 0x06).Bytes());
                stream.AddRange(Sbep.Encode(0x21, new byte[] { 2, 0x07, 0x0E, 1, 2 }));
                List<SbItem> items = FeedAll(stream.ToArray());
                var rx = items.Where(i => i.Kind == SbItem.Kinds.Rx).ToList(); var sbep = items.Where(i => i.Kind == SbItem.Kinds.Sbep).ToList();
                Check(items.Count(i => i.Kind == SbItem.Kinds.Raw) == 1 && rx.Count == 5 && sbep.Count == 2, "Bus parser framing failed (" + items.Count + " items).");
                Check(SbDecoder.Describe(rx[0].Frame, xtl.Bus.HeadButtons).Category == "TX" && SbDecoder.Describe(rx[1].Frame, null).Value == 3 && SbDecoder.Describe(rx[3].Frame, null).Category == "MODE", "RADKEY/SQLDET/ACTMDU decode failed.");
                SbInfo disp = SbDecoder.DescribeSbep(sbep[0].Raw); Check(disp.Category == "DISP" && disp.Str == "FIRE DISP 1   " && disp.Code == 0, "SBEP display decode failed (extended length).");
                SbInfo ind = SbDecoder.DescribeSbep(sbep[1].Raw); Check(ind.Category == "IND" && ind.Codes.Length == 2 && ind.Codes[0] == 0x07 && ind.States[1] == 2, "SBEP indicator decode failed.");
                SbInfo btn = SbDecoder.Describe(new SbFrame(0x05, 0x61, 0x01, 0x57), xtl.Bus.HeadButtons); Check(btn.Category == "BTN" && btn.Name == "HOME", "Head button decode failed.");
                // ---- real consolette traffic (bench log 20 Oct 2026), including EPREQ twice in a row ----
                string epreq = "00 12 05 06 39 ";
                var bench = new List<byte>();
                bench.AddRange(H(epreq + epreq + "1F 00 14 80 08 07 00 00 46 44 31 20 52 50 54 00 00 00 00 00 00 00 6C"));       // FD1 RPT
                bench.AddRange(H("01 00 01 1F 71 05 61 01 57 31"));                                                                     // mode 1, HOME pressed
                bench.AddRange(H(epreq + "1F 00 14 80 08 07 00 00 43 4E 54 59 20 4E 4F 00 00 00 00 00 00 00 42"));                    // CNTY NO (7 chars)
                bench.AddRange(H(epreq + "F4 21 01 02 01 E6"));                                                                         // indicator 0x02 on
                bench.AddRange(H(epreq + "1F 00 14 80 08 07 00 01 49 54 59 20 46 44 20 00 00 00 00 00 00 00 7C"));                    // col 1 'ITY FD '
                List<SbItem> benchItems = FeedAll(bench.ToArray());
                var benchRx = benchItems.Where(i => i.Kind == SbItem.Kinds.Rx).ToList(); var benchSbep = benchItems.Where(i => i.Kind == SbItem.Kinds.Sbep).ToList();
                Check(benchItems.Count(i => i.Kind == SbItem.Kinds.Raw) == 0 && benchRx.Count == 7 && benchSbep.Count == 4, "Real bench traffic: a doubled EPREQ must not lose frames (" + benchRx.Count + " frames, " + benchSbep.Count + " SBEP, " + benchItems.Count(i => i.Kind == SbItem.Kinds.Raw) + " raw).");
                SbInfo b0 = SbDecoder.DescribeSbep(benchSbep[0].Raw), b1 = SbDecoder.DescribeSbep(benchSbep[1].Raw), b2 = SbDecoder.DescribeSbep(benchSbep[2].Raw), b3 = SbDecoder.DescribeSbep(benchSbep[3].Raw);
                Check(b0.Str == "FD1 RPT" && b1.Str == "CNTY NO" && b2.Codes[0] == 0x02 && SbDecoder.IndicatorName(2) == "SCAN" && b3.Code == 1 && b3.Str == "ITY FD ", "Real bench SBEP decode failed.");
                Check(SbDecoder.Describe(benchRx[2].Frame, null).Category == "MODE" && SbDecoder.Describe(benchRx[2].Frame, null).Value == 1 && SbDecoder.Describe(benchRx[3].Frame, xtl.Bus.HeadButtons).Name == "HOME", "Real bench frame decode failed.");
                // The radio sends only the characters that change (bench log 2): MURS 1 -> col 5 '2', CNTY SO -> col 5 'NO '.
                MethodInfo splice = typeof(ConsoleWindow).GetMethod("Splice", BindingFlags.NonPublic | BindingFlags.Static);
                Func<string, int, string, string> sp = (line, col, text) => (string)splice.Invoke(null, new object[] { line, col, text });
                Check(sp("MURS 1  ", 5, "2") == "MURS 2  " && sp("CNTY SO ", 5, "NO ") == "CNTY NO " && sp("FD4     ", 4, "FG") == "FD4 FG  "
                    && sp("RIVER   ", 0, "CNTY SO") == "CNTY SO " && sp(sp("NC EMA 1", 7, "2"), 7, "3") == "NC EMA 3", "Partial display updates from the radio were not merged.");
                SbInfo monOn = SbDecoder.Describe(new SbFrame(0x01, 0x05, 0x01, 0x3C), null), monOff = SbDecoder.Describe(new SbFrame(0x01, 0x05, 0x00, 0x3C), null);
                Check(monOn.Category == "MON" && monOn.Value == 1 && monOff.Category == "MON" && monOff.Value == 0, "Radio monitor report (DISPLY field 0x05) must drive the MON light.");
                SbFrame f; string err; Check(SbHex.TryFrame("05 61 02 57", out f, out err) && f.Crc == SbCrc.Compute(new byte[] { 5, 0x61, 2, 0x57 }, 0, 4) && !SbHex.TryFrame("05 61 02 57 00", out f, out err), "Hex frame parse/CRC check failed.");
                Check(SbPattern.Matches(new SbFrame(1, 0x22, 3, 0x1E), "01 ** 03 1E") && !SbPattern.Matches(new SbFrame(1, 0x22, 0, 0x1E), "01 ** 03 1E"), "Bus rule pattern matching failed.");
                var sim = new Sb9600Link(); sim.SimChannels = new List<string> { "DISPATCH", "FIREGROUND" }; sim.OpenSimulated(xtl.Bus);
                // Like the real bench radio: display text first, then the mode report (numbered from 1) about 600 ms later.
                sim.Button(0x51, 2); System.Threading.Thread.Sleep(300); List<SbItem> simItems = sim.Drain();
                bool displayFirst = simItems.Any(i => i.Kind == SbItem.Kinds.Sbep && SbDecoder.DescribeSbep(i.Raw).Str.Trim() == "FIREGROUND") && !simItems.Any(i => i.Kind == SbItem.Kinds.Rx && i.Frame.Func == 0x1F);
                System.Threading.Thread.Sleep(800); simItems.AddRange(sim.Drain()); sim.Close();
                Check(simItems.Any(i => i.Kind == SbItem.Kinds.Tx) && displayFirst && simItems.Any(i => i.Kind == SbItem.Kinds.Rx && i.Frame.Func == 0x1F && i.Frame.P2 == 2), "Simulated XTL did not answer MODE UP (display, then mode 2).");

                // ---- recorder ----
                var rec = new CallRecorder(); rec.Settings = new RecorderSettings { HangMs = 100, ThresholdDb = -40, DiskLogging = false };
                short[] loud = ConsoleWave.Alert(new AlertProfile { Kind = AlertKind.Steady, FrequencyA = 800, DurationMs = 1000 }, 80), quiet = new short[1920];
                for (int i = 0; i + 1920 <= loud.Length; i += 1920) rec.AddRx("XTL", "XTL", loud.Skip(i).Take(1920).ToArray(), 1920);
                System.Threading.Thread.Sleep(150); rec.AddRx("XTL", "XTL", quiet, quiet.Length);
                Check(rec.Calls().Count == 1 && Math.Abs(rec.Calls()[0].Seconds - 1.04) < 0.1, "RX call detection failed.");
                rec.BeginTx("XTL", "XTL"); rec.AddTx("XTL", loud, loud.Length); rec.EndTx("XTL");
                Check(rec.Calls().Count == 2 && rec.Calls()[1].Kind == "TX" && CallRecorder.Join(rec.Calls()).Length > 16000 * 2, "TX recall failed.");
                rec.ChannelOf = (id, kind, start) => kind == "TX" ? "STATE FD (Statewide Fire)" : "CITY FD (City Fire Department)";
                rec.BeginTx("XTL", "XTL"); rec.AddTx("XTL", loud, loud.Length); rec.EndTx("XTL");
                Check(rec.Calls()[2].Channel == "STATE FD (Statewide Fire)" && rec.Calls()[2].ToString().Contains("STATE FD"), "Recordings must carry the channel, not just the radio name.");
                // 90-day clean-up touches only the recorder's own day folders and files
                string recRoot = Path.Combine(temp, "Recordings"); string oldDay = Path.Combine(recRoot, "2026-01-02"), newDay = Path.Combine(recRoot, "2026-10-20"), other = Path.Combine(recRoot, "notes");
                foreach (string d in new string[] { oldDay, newDay, other }) Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(oldDay, "080000_000_XTL_RX.wav"), "x"); File.WriteAllText(Path.Combine(oldDay, "index.csv"), "x"); File.WriteAllText(Path.Combine(oldDay, "keep.txt"), "x");
                File.WriteAllText(Path.Combine(newDay, "090000_000_XTL_TX.wav"), "x"); File.WriteAllText(Path.Combine(other, "old.wav"), "x");
                int purged = CallRecorder.Purge(recRoot, 90, new DateTime(2026, 10, 21));
                Check(purged == 2 && File.Exists(Path.Combine(oldDay, "keep.txt")) && File.Exists(Path.Combine(newDay, "090000_000_XTL_TX.wav")) && File.Exists(Path.Combine(other, "old.wav")), "Recording clean-up must delete only recorder WAV/CSV files older than KeepDays (" + purged + ").");
                File.Delete(Path.Combine(oldDay, "keep.txt")); CallRecorder.Purge(recRoot, 90, new DateTime(2026, 10, 21)); Check(!Directory.Exists(oldDay), "An emptied old day folder must be removed.");
                Check(CallRecorder.Purge(recRoot, 0, new DateTime(2030, 1, 1)) == 0 && File.Exists(Path.Combine(newDay, "090000_000_XTL_TX.wav")), "KeepDays 0 must keep recordings forever.");
                Check(new RecorderSettings().DiskLogging && new RecorderSettings().KeepDays == 90 && new RecorderSettings().AutoStartRx, "Recorder defaults must be: disk logging on, 90 days, receive audio starts automatically.");
                var footCfg = DispatchConfig.Load(Path.Combine(temp, "foot.config.xml")); footCfg.Radios[0].Bus.PortName = "COM3"; footCfg.Footswitch.Source = FootswitchSource.ComPortLine; footCfg.Footswitch.PortName = "com3";
                bool footBad = false; try { footCfg.Validate(); } catch (InvalidDataException) { footBad = true; } Check(footBad, "The footswitch must not share the RIB COM port.");
                footCfg.Footswitch.Source = FootswitchSource.KeyboardKey; footCfg.Footswitch.Key = "F13"; footCfg.Validate();
                Check(Hotkey.Parse("F13") == Keys.F13 && Hotkey.Parse("Scroll") == Keys.Scroll && Hotkey.Parse("ScrollLock") == Keys.Scroll && Hotkey.Parse("Pause") == Keys.Pause, "Pedal keys F13 / Scroll / Pause must parse.");
                foreach (string badKey in new string[] { "Ctrl+F13", "", "Escape" })
                {
                    footCfg.Footswitch.Key = badKey; footBad = false; try { footCfg.Validate(); } catch (InvalidDataException) { footBad = true; }
                    Check(footBad, "Footswitch key '" + badKey + "' must be refused (modifier, blank, or a console hotkey).");
                }
                Check(new DispatchConfig().StartIn == StartupMode.Live && !new DispatchConfig().RequireArm, "A new console must start Live with no ARM step.");
                // settings import: the config and the receive-audio files come across; anything else stays behind
                string oldVer = Path.Combine(temp, "old_version"), shared = Path.Combine(temp, "shared"); Directory.CreateDirectory(oldVer); Directory.CreateDirectory(shared);
                footCfg.Footswitch.Source = FootswitchSource.Off; footCfg.ConsoleName = "IMPORT TEST"; footCfg.Save(Path.Combine(oldVer, AppPaths.ConfigName));
                File.WriteAllText(Path.Combine(oldVer, "audio.settings"), "monitor_input=Line In"); File.WriteAllText(Path.Combine(oldVer, "audio_RADIO2.settings"), "x"); File.WriteAllText(Path.Combine(oldVer, "other.settings"), "x");
                File.WriteAllText(Path.Combine(shared, AppPaths.ConfigName), "<old/>");
                AppPaths.CopySettings(Path.Combine(oldVer, AppPaths.ConfigName), shared);
                Check(DispatchConfig.Load(Path.Combine(shared, AppPaths.ConfigName)).ConsoleName == "IMPORT TEST" && File.Exists(Path.Combine(shared, "audio.settings")) && File.Exists(Path.Combine(shared, "audio_RADIO2.settings"))
                    && !File.Exists(Path.Combine(shared, "other.settings")) && File.ReadAllText(Path.Combine(shared, AppPaths.ConfigName + ".before-import")) == "<old/>", "Settings import must copy the settings and receive-audio files and keep the replaced settings.");
                Check(AppPaths.FirstRunImport(shared) == "", "First-run import must never overwrite settings that already exist.");

                // ---- 7.1 onboard jack routing for TX ----
                int chs, sa, sb; uint mask;
                WinMm.JackLayout(TxJack.Side71, false, out chs, out mask, out sa, out sb); Check(chs == 8 && mask == 0x63F && sa == 6 && sb == 7, "Side jack must be channels 7/8 of 7.1.");
                WinMm.JackLayout(TxJack.Rear71, true, out chs, out mask, out sa, out sb); Check(chs == 6 && mask == 0x3F && sa == 4 && sb == 5, "Rear jack must be channels 5/6 of 5.1.");
                var inter = new short[4 * 8]; WinMm.Interleave(new short[] { 100, -200, 300, -400 }, 4, 8, 6, 7, inter);
                Check(inter[6] == 100 && inter[7] == 100 && inter[8 + 6] == -200 && inter.Where((v, k) => k % 8 < 6).All(v => v == 0), "TX audio leaked onto non-TX jacks.");
                Check(System.Runtime.InteropServices.Marshal.SizeOf(typeof(WinMm.FormatExt)) == 40 && System.Runtime.InteropServices.Marshal.SizeOf(typeof(WinMm.Format)) == 18, "WAVEFORMAT struct sizes wrong.");
                Check(config.ConsoleName == Brand.Department && new UiSettings().ThemeName == "Black & Gold", "Branding defaults missing.");

                // ---- CPS codeplug import (the example export shipped in cps\) ----
                string cpsFile = Path.Combine("cps", "Example_XTL5000_Channels.csv");
                if (File.Exists(cpsFile))
                {
                    CpsImportResult cpsResult = CpsImport.Parse(File.ReadAllLines(cpsFile));
                    List<ChannelProfile> imported = CpsImport.ToChannels(cpsResult, CpsImport.LoadNames("cps"), new HashSet<int> { 3, 6 }, true);
                    Check(imported.Count == 59 && imported.Count(c => c.ZoneNumber == 5) == 13 && imported.Count(c => c.ZoneNumber == 4) == 9, "CPS import: zone/channel counts wrong.");
                    Check(cpsResult.Scan.Count == 15 && imported.Count(c => c.ScanMember) == 15 && imported.Count(c => c.RxOnly) == 18, "CPS import: scan or RX-only counts wrong.");
                    ChannelProfile ott = imported.First(c => c.RadioText == "CITY FD");
                    Check(ott.ZoneNumber == 1 && ott.ZonePosition == 5 && ott.Name == "City Fire Department" && Math.Abs(ott.RxMHz - 151.625) < 1e-6 && ott.TxTone == "PL 186.2" && !ott.RxOnly, "CPS import: CITY FD fields wrong.");
                    Check(imported.First(c => c.ScanPriority == 1).RadioText == "FD1 RPT" && imported.First(c => c.ScanPriority == 2).RadioText == "STATE FD" && imported.First(c => c.RadioText == "MA RED").RxOnly, "CPS import: priorities / mutual-aid RX-only wrong.");
                    Check(imported.All(c => c.FunctionHz == 1950) && imported.First(c => c.RadioText == "FD1 OPS").RxTone == "DPL 032", "CPS import: tone table / DPL wrong.");
                    Check(imported.First(c => c.RadioText == "STATE FD").Name == "Statewide Fire" && imported.First(c => c.RadioText == "RIVER").Name == "River Rescue" && imported.All(c => c.Name.Length > 0), "Example channel names missing.");
                    var cpsRadio = DispatchConfig.NewRadio("CPS"); cpsRadio.Channels.Clear(); cpsRadio.Channels.AddRange(imported);
                    int z1FireMa = imported.FindIndex(c => c.ZoneNumber == 1 && c.RadioText == "FIRE MA"), z3FireMa = imported.FindIndex(c => c.ZoneNumber == 3 && c.RadioText == "FIRE MA");
                    Check(DisplayText.FindChannel(cpsRadio, "FIRE MA") == -1 && DisplayText.Expand(cpsRadio, "FIRE MA") == "FIRE MA" && ConsoleWindow.LocateDisplayed(cpsRadio, "FIRE MA", 3) == z3FireMa && ConsoleWindow.LocateDisplayed(cpsRadio, "FIRE MA", 1) == z1FireMa, "Duplicate FIRE MA must resolve by zone, never by guessing.");
                    Check(DisplayText.FindChannel(cpsRadio, "CITY FD") == 4 && DisplayText.Expand(cpsRadio, "CITY FD") == "City Fire Department", "CITY FD display expansion failed.");
                    Check(ConsoleWindow.ChannelFromArg(cpsRadio, "CITY FD") == 4 && ConsoleWindow.ChannelFromArg(cpsRadio, "Z3/1") == z3FireMa && ConsoleWindow.ChannelFromArg(cpsRadio, "1/2") == 1 && ConsoleWindow.ChannelFromArg(cpsRadio, "5") == 4 && ConsoleWindow.ChannelFromArg(cpsRadio, "River Rescue") >= 0 && ConsoleWindow.ChannelFromArg(cpsRadio, "Z9/9") == -1, "chan: argument forms failed.");
                    // first run beside a cps folder loads the codeplug and the example quick keys
                    string firstRun = Path.Combine(temp, "firstrun"); Directory.CreateDirectory(Path.Combine(firstRun, "cps"));
                    foreach (string cpsCopy in Directory.GetFiles("cps")) File.Copy(cpsCopy, Path.Combine(firstRun, "cps", Path.GetFileName(cpsCopy)));
                    DispatchConfig seeded = DispatchConfig.Load(Path.Combine(firstRun, "console.config.xml")); seeded.Validate();
                    RadioProfile sr = seeded.Radios[0];
                    Check(SbDecoder.ButtonCode("MODE UP", sr.Bus.HeadButtons) == 0x51 && SbDecoder.ButtonCode("MODE DN", sr.Bus.HeadButtons) == 0x50 && SbDecoder.ButtonCode("HOME", sr.Bus.HeadButtons) == 0x61
                        && SbDecoder.ButtonCode("PTT", sr.Bus.HeadButtons) == 0x01 && SbDecoder.ButtonCode("SEL", sr.Bus.HeadButtons) == 0x67 && SbDecoder.ButtonCode("DIM", sr.Bus.HeadButtons) == 0x60, "First run lost the standard W7 keys (MODE UP/DN, HOME, PTT) or the bench W7 Sel/DIM codes.");
                    // a settings file saved by the buggy build (only the W7 bench extras) is repaired on load
                    var broken = DispatchConfig.Load(Path.Combine(firstRun, "console.config.xml")); broken.Radios[0].Bus.HeadButtons.RemoveAll(b => b.Name != "ZONE UP" && b.Name != "ZONE DN" && b.Name != "MON" && b.Name != "SCAN");
                    broken.Save(Path.Combine(firstRun, "broken.config.xml")); var repaired = DispatchConfig.Load(Path.Combine(firstRun, "broken.config.xml")).Radios[0].Bus.HeadButtons;
                    Check(SbDecoder.ButtonCode("MODE UP", repaired) == 0x51 && SbDecoder.ButtonCode("HOME", repaired) == 0x61 && SbDecoder.ButtonCode("SEL", repaired) == 0x67 && SbDecoder.ButtonCode("ZONE UP", repaired) == 0x31, "Head buttons were not repaired on load.");
                    Check(sr.Bus.IndicatorDirect == 0x06 && sr.Bus.UnusedKeys.Contains("TOP1") && SbDecoder.ButtonCode("NUIS DEL", sr.Bus.HeadButtons) == 0x3B && SbDecoder.ButtonCode("ZONE UP", sr.Bus.HeadButtons) == 0x31 && SbDecoder.ButtonCode("SCAN", sr.Bus.HeadButtons) == 0x65, "Bench W7 defaults (DIR light, unused keys, zone/scan/nuisance keys) missing.");
                    Check(sr.Channels.Count == 59 && sr.Channels[sr.HomeChannel].RadioText == "STATE FD" && sr.SelectedChannel == sr.HomeChannel && sr.ChannelSelect == ChannelMethod.Sb9600ModeKeys && seeded.SeedNote.Length > 0, "First-run codeplug load failed.");
                    Check(seeded.QuickKeys.Count == 10 && seeded.QuickKeys.Exists(q => q.Action == "func:SCAN" && q.Hotkey == "F5") && seeded.QuickKeys.Exists(q => q.Action == "func:NUIS DEL" && q.Hotkey == "F6") && seeded.QuickKeys.All(q => Hotkey.Parse(q.Hotkey) != Keys.None) && seeded.QuickKeys.Where(q => q.Action.StartsWith("chan:")).All(q => ConsoleWindow.ChannelFromArg(sr, q.Action.Substring(5)) >= 0), "Example quick keys must parse and point at real channels.");
                    // a v0.6.1 settings file (FunctionHz 0, Manual select, FromCodeplug) converts on load and is saved in the new form
                    string legacyPath = Path.Combine(firstRun, "legacy.config.xml");
                    DispatchConfig.Load(Path.Combine(temp, "legacy-src.xml")).Save(legacyPath);
                    string legacyXml = File.ReadAllText(legacyPath).Replace("<ChannelProfile>", "<ChannelProfile><FromCodeplug>true</FromCodeplug>").Replace("<ChannelSelect>TrcTone</ChannelSelect>", "<ChannelSelect>Manual</ChannelSelect>");
                    legacyXml = System.Text.RegularExpressions.Regex.Replace(legacyXml, "<FunctionHz>\\d+</FunctionHz>", "<FunctionHz>0</FunctionHz>");
                    Check(legacyXml.Contains("<FunctionHz>0</FunctionHz>") && legacyXml.Contains("<ChannelSelect>Manual</ChannelSelect>"), "Legacy test file not built.");
                    File.WriteAllText(legacyPath, legacyXml);
                    DispatchConfig migrated61 = DispatchConfig.Load(legacyPath);
                    Check(migrated61.Radios[0].Channels.Count == 59 && migrated61.Radios[0].ChannelSelect == ChannelMethod.Sb9600ModeKeys && migrated61.Radios[0].Channels.All(c => c.FunctionHz == 1950) && migrated61.SeedNote.Contains("v0.6.1"), "v0.6.1 settings were not converted.");
                    migrated61.Save(legacyPath); Check(!File.ReadAllText(legacyPath).Contains("FromCodeplug") && !File.ReadAllText(legacyPath).Contains("<ReceiveOnly>"), "Legacy field names must not be written back.");
                    seeded.Save(Path.Combine(firstRun, "console.config.xml")); Check(DispatchConfig.Load(Path.Combine(firstRun, "console.config.xml")).Radios[0].Channels.Count == 59, "Seeded codeplug did not persist.");
                }

                // ---- keyboard shortcuts: the text on a button must be the key that triggers it ----
                Check(Hotkey.Parse("Ctrl+1") == (Keys.Control | Keys.D1) && Hotkey.Format(Keys.Control | Keys.D1) == "Ctrl+1", "Ctrl+1 must parse (KeysConverter alone reads it as nothing).");
                Check(Hotkey.Parse("shift+f12") == (Keys.Shift | Keys.F12) && Hotkey.Parse("PgUp") == Keys.PageUp && Hotkey.Parse("Esc") == Keys.Escape && Hotkey.Parse("Control+D2") == (Keys.Control | Keys.D2) && Hotkey.Parse("num5") == Keys.NumPad5, "Hotkey aliases failed.");
                Check(Hotkey.Parse("Ctrl+") == Keys.None && Hotkey.Parse("Bogus") == Keys.None && Hotkey.Parse("5x") == Keys.None && Hotkey.Parse("Ctrl+Shift") == Keys.None && Hotkey.Parse("F25") == Keys.None && Hotkey.Parse("") == Keys.None, "Bad hotkeys must not parse.");
                foreach (Keys k in new Keys[] { Keys.F1, Keys.F12 | Keys.Shift, Keys.PageUp, Keys.PageDown, Keys.Escape, Keys.Control | Keys.D9, Keys.Alt | Keys.P, Keys.NumPad5, Keys.Return, Keys.OemMinus | Keys.Control, Keys.Home, Keys.Space, Keys.Control | Keys.Shift | Keys.F5 })
                    Check(Hotkey.Parse(Hotkey.Format(k)) == k, "Hotkey round trip failed for " + k + ".");
                var hk = new HotkeySettings(); string[] globals = { hk.Ptt, hk.GroupPtt, hk.StopAll, hk.ChannelUp, hk.ChannelDown, hk.NextRadio, hk.PreviousRadio, hk.Monitor, hk.ReplayLast };
                // Blank = deliberately unassigned (v0.8.1: Monitor, because the MONITOR quick key has F4). Every assigned key must parse and be unique.
                string[] assigned = globals.Where(g => !String.IsNullOrWhiteSpace(g)).ToArray();
                Check(String.IsNullOrEmpty(hk.Monitor) && assigned.Length == globals.Length - 1, "Only the extra Monitor key should be blank by default.");
                Check(assigned.All(g => Hotkey.Parse(g) != Keys.None) && assigned.Select(g => Hotkey.Parse(g)).Distinct().Count() == assigned.Length, "Default global hotkeys must parse and be unique.");
                Check(config.QuickKeys.All(q => String.IsNullOrEmpty(q.Hotkey) || (Hotkey.Parse(q.Hotkey) != Keys.None && !globals.Any(g => Hotkey.Parse(g) == Hotkey.Parse(q.Hotkey)))), "Default quick-key hotkeys must parse and not collide with global keys.");
                Check(Hotkey.Reserved(Keys.Alt | Keys.F4) && !Hotkey.Reserved(Keys.F12), "Reserved-key check wrong.");

                // ---- short radio display -> full names ----
                var named = DispatchConfig.NewRadio("Names");
                named.Channels.Clear(); named.Channels.Add(new ChannelProfile { Name = "CITY FIRE DEPT", RadioText = "CITY FD" }); named.Channels.Add(new ChannelProfile { Name = "FIREGROUND 2" });
                named.Bus.DisplayLookups.Add(new TextLookup("CITY PD", "CITY POLICE")); named.Bus.DisplayLookups.Add(new TextLookup("TAC", "TACTICAL") { PartialWord = true });
                Check(DisplayText.FindChannel(named, "  city   fd ") == 0 && DisplayText.Expand(named, "CITY FD  ") == "CITY FIRE DEPT", "Radio text must select and expand the channel.");
                Check(DisplayText.FindChannel(named, "FIREGROUND 2") == 1 && DisplayText.Expand(named, "CITY PD") == "CITY POLICE" && DisplayText.Expand(named, "TAC 3") == "TACTICAL 3" && DisplayText.Expand(named, "STACK") == "STACK", "Display lookups failed.");

                var job = new PageJob(config.Radios[0], config.Alerts[0]); config.Alerts[0].FrequencyA = 1234; Check(job.Alert.FrequencyA == 1000, "Queued alert must snapshot preset.");

                if (args.Length > 0 && args[0].StartsWith("--ui")) UiChecks(temp);
                Console.WriteLine("PASS: " + _count + " dispatch-console checks."); return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL: " + (ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException.ToString() : ex.ToString())); return 1; }
        }

        private static Button FindButton(Control root, string prefix)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Button && c.Text.StartsWith(prefix) && c.Visible) return (Button)c;
                Button inner = FindButton(c, prefix); if (inner != null) return inner;
            }
            return null;
        }
        private static void Pump(Form window, int ms)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { Call(window, "Tick"); Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        }

        private static void UiChecks(string temp)
        {
            Application.EnableVisualStyles();
            using (var window = new ConsoleWindow(Path.Combine(temp, "ui.config.xml"), true))
            {
                // Bench-test behaviour for the checks below: ARM step on, no receive devices or disk files on the test PC.
                var setup = (DispatchConfig)Field(window, "_config"); setup.RequireArm = true; setup.Recorder.AutoStartRx = false; setup.Recorder.DiskLogging = false;
                window.Show(); Application.DoEvents();
                Check((ConsoleWindow.OpMode)Field(window, "_opMode") == ConsoleWindow.OpMode.Simulation, "The self-test window must start in Test mode.");
                var radio = (RadioProfile)Field(window, "_selected"); var engine = (TxController)Field(window, "_tx");
                var states = (Dictionary<string, RadioState>)Field(window, "_states");
                Call(window, "StartPtt", "SELECTED"); Check(engine.Active && engine.Targets.Contains(radio.Id), "Selected PTT failed.");
                int channel = radio.SelectedChannel; Call(window, "ChangeChannel", (channel + 1) % radio.Channels.Count); Check(radio.SelectedChannel == channel, "Channel must not change during TX.");
                Call(window, "StopTx", "test release"); Check(!engine.Active, "PTT release failed.");
                Call(window, "HeadKey", "MENU"); Check(states[radio.Id].MenuOpen, "Menu did not open.");
                Call(window, "HeadKey", "RIGHT"); Check(states[radio.Id].MenuIndex == 1, "Menu navigation failed.");
                Call(window, "HeadKey", "SELECT"); Check(states[radio.Id].Monitor && !states[radio.Id].MenuOpen, "Menu selection failed.");
                Call(window, "Function", "SCAN"); Check(states[radio.Id].Scan, "Scan simulation failed.");
                Call(window, "Inject", "EMERGENCY"); Check(states[radio.Id].Emergency, "Emergency injection failed.");
                Call(window, "Function", "ACK EMERG"); Check(!states[radio.Id].Emergency, "Emergency acknowledgement failed.");
                Call(window, "Enqueue"); Call(window, "Enqueue"); Call(window, "StartQueue"); Check(engine.Active, "Paging queue did not start.");
                Call(window, "StopAll", "test ESC"); Check(!engine.Active && !(bool)Field(window, "_queueRunning"), "Global cancellation failed.");
                // simulated SB9600 PTT path
                radio.Ptt = PttMethod.Sb9600Button; Call(window, "StartPtt", "SELECTED"); Pump(window, 300);
                Check(states[radio.Id].BusTx, "Simulated bus did not report TX after SB9600 PTT.");
                Call(window, "StopTx", "bus release"); Pump(window, 300); Check(!states[radio.Id].BusTx, "Simulated bus did not report unkey.");
                radio.Ptt = PttMethod.TrcTones;
                // bus mode keys + display sync
                radio.ChannelSelect = ChannelMethod.Sb9600ModeKeys; Call(window, "ChangeChannel", 2); Pump(window, 2500);
                Check(states[radio.Id].BusChannel == 2 + radio.Bus.ChannelBase && states[radio.Id].BusDisplay.Trim() == radio.Channels[2].Name, "Bus mode stepping / display mirror failed.");
                // The simulated XTL, like the real one, reports its mode ~600 ms after the last key. Let that report land first,
                // or it moves the console back to the radio's channel in the middle of the macro test (timing-dependent failure).
                Pump(window, 1500);
                radio.ChannelSelect = ChannelMethod.TrcTone;
                // macro: wait for the macro to finish instead of a fixed time
                states[radio.Id].LowPower = false;
                Call(window, "RunAction", "chan:1; wait:50; func:LOW PWR", "test");
                var macroSteps = (System.Collections.IList)Field(window, "_macro");
                for (int wait = 0; wait < 150 && macroSteps.Count > 0; wait++) Pump(window, 20);
                Pump(window, 100);
                Check(radio.SelectedChannel == 0 && states[radio.Id].LowPower, "Macro execution failed (channel " + (radio.SelectedChannel + 1) + ", LOW PWR " + states[radio.Id].LowPower + ", steps left " + macroSteps.Count + ").");
                // receive-only mode must refuse to transmit
                Call(window, "SetMode", ConsoleWindow.OpMode.LiveReceive); Call(window, "StartPtt", "SELECTED"); Check(!engine.Active, "Receive-only mode must not transmit.");
                Call(window, "SetMode", ConsoleWindow.OpMode.Live); Call(window, "StartPtt", "SELECTED"); Check(!engine.Active, "Live mode must not transmit before ARM.");
                // normal operation: no ARM step, ready as soon as Live opens
                setup.RequireArm = false; Call(window, "SetMode", ConsoleWindow.OpMode.Live); Check((bool)Field(window, "_armed"), "Live with RequireArm off must be ready to talk without ARM TX.");
                setup.RequireArm = true;
                Call(window, "SetMode", ConsoleWindow.OpMode.Simulation); Check(!(bool)Field(window, "_armed"), "Test mode must clear the armed state.");
                // footswitch PTT: press keys the radio, release unkeys, losing the pedal unkeys
                window.FootTest = true; Pump(window, 120); Check(engine.Active && (bool)Field(window, "_footHeld"), "Footswitch press did not key PTT.");
                window.FootTest = false; Pump(window, 120); Check(!engine.Active, "Footswitch release did not unkey.");
                window.FootTest = true; Pump(window, 120); window.FootTest = null; Pump(window, 120); Check(!engine.Active && !(bool)Field(window, "_footHeld"), "Losing the footswitch must unkey.");
                // every theme rebuilds
                var cfg = (DispatchConfig)Field(window, "_config");
                foreach (string theme in UiTheme.Names) { cfg.Ui.ThemeName = theme; Call(window, "BuildUi"); Application.DoEvents(); }
                cfg.Ui.Placement = CardPlacement.Top; cfg.Ui.FontScale = 150; cfg.Ui.HiddenTabs = "MIXER;RECORDER"; Call(window, "BuildUi"); Application.DoEvents();
                Call(window, "StartPtt", "SELECTED"); Check(engine.Active, "PTT after rebuild failed."); Call(window, "StopTx", "done");
                // ---- small screens: the Compact layout fits a 1024x768 laptop ----
                cfg.Ui = new UiSettings(); cfg.Ui.Layout = LayoutMode.Compact; Call(window, "BuildUi"); window.WindowState = FormWindowState.Normal; window.ClientSize = new Size(1008, 690); Application.DoEvents();
                Control compactTabs = window.Controls.Find("CompactOperations", true).FirstOrDefault();
                Check(compactTabs is TabControl && ((TabControl)compactTabs).TabPages.Count == (cfg.Radios.Count > 1 ? 3 : 2) && ((LcdPanel)Field(window, "_lcd")).Compact, "Compact layout must put channels, alerts (and radios, when there are several) on tabs.");
                foreach (TabPage page in ((TabControl)compactTabs).TabPages) Check(TextRenderer.MeasureText(page.Text, compactTabs.Font).Width <= ((TabControl)compactTabs).ItemSize.Width, "Compact tab title is cut off: " + page.Text);
                Call(window, "ShowTool", "ALERTS"); Application.DoEvents(); Check(((Control)Field(window, "_alertSurface")).Visible, "ALERTS did not select its tab in the Compact layout.");
                Call(window, "ShowTool", "CHANNELS"); Application.DoEvents(); Control keysPanel = (Control)Field(window, "_channelKeys");
                if (!(keysPanel.Visible && keysPanel.Height >= ChannelKeyButton.PreferredHeight((UiTheme)Field(window, "_t"))))
                {
                    // Report where the height went, so a failure on a real screen can be fixed exactly.
                    var rootPanel = (TableLayoutPanel)Field(window, "_rootControl"); var lcdPanel = (Control)Field(window, "_lcd");
                    string why = "window client " + window.ClientSize.Width + "x" + window.ClientSize.Height + ", state " + window.WindowState
                        + "; rows (header, menu, main, quick keys, PTT, journal, footer) = " + String.Join(", ", rootPanel.GetRowHeights().Select(h => h.ToString()).ToArray())
                        + "; display area rows = " + (lcdPanel.Parent is TableLayoutPanel ? String.Join(", ", ((TableLayoutPanel)lcdPanel.Parent).GetRowHeights().Select(h => h.ToString()).ToArray()) : "?")
                        + "; tabs " + compactTabs.Height + " (pages " + ((TabControl)compactTabs).DisplayRectangle.Height + "); channel keys " + keysPanel.Height + " needs " + ChannelKeyButton.PreferredHeight((UiTheme)Field(window, "_t"))
                        + "; quick keys " + ((Control)Field(window, "_quick")).Controls.Count + "; radios " + cfg.Radios.Count + "; screen " + Screen.FromControl(window).WorkingArea.Size;
                    Check(false, "Channel keys have no room in the Compact layout: " + why);
                }
                var cq = (FlowLayoutPanel)Field(window, "_quick"); int qRight = cq.Controls.Cast<Control>().Max(c => c.Bottom);
                Check(qRight <= cq.ClientSize.Height + 2, "Compact quick-key bar cuts off keys.");
                foreach (Control row in new Control[] { FindButton(window, "CH +").Parent, FindButton(window, "HOLD TO TALK").Parent })
                    Check(row.Controls.Cast<Control>().Where(c => c.Visible).All(c => c.Right <= row.ClientSize.Width + 1), "A Compact row is wider than a 1024 px screen: " + row.Name);
                Check(window.MinimumSize.Width <= 800 && window.MinimumSize.Height <= 560, "Minimum window size too large for small screens.");
                cfg.Ui = new UiSettings(); cfg.Ui.Layout = LayoutMode.Full; Call(window, "BuildUi"); window.WindowState = FormWindowState.Maximized; Application.DoEvents();
                // ---- operations view (menus, tool windows); one button per control ----
                Check(window.MainMenuStrip != null && window.MainMenuStrip.Items.Count >= 4, "Operating menus missing.");
                Check(((Control)Field(window, "_alertSurface")).Visible && ((Control)Field(window, "_channelKeys")).Visible, "Channel keys and alerts must be on screen.");
                var mainButtons = new List<Button>(); var stack = new Stack<Control>(); stack.Push(window);
                while (stack.Count > 0) { Control c = stack.Pop(); if (ReferenceEquals(c, Field(window, "_quick"))) continue; if (c is Button && c.Visible) mainButtons.Add((Button)c); foreach (Control child in c.Controls) stack.Push(child); }
                foreach (string fn in new string[] { "MON", "SCAN", "NUIS DEL" })
                    Check(!mainButtons.Any(b => b.Text.Split('\n')[0].Trim() == fn), fn + " has a second button on the main screen; it must only be a quick key.");
                Check(mainButtons.Count(b => b.Text.StartsWith("HOME")) == 1, "HOME must appear once on the main screen.");
                Check(!mainButtons.Any(b => b.Text.StartsWith("EXPORT") || b.Text.StartsWith("INJECT")), "Test buttons (EXPORT, INJECT) must not be on the main screen.");
                // tone sets: ShowOnMain decides the alert / tone keys
                int keysBefore = ((FlowLayoutPanel)Field(window, "_alertButtons")).Controls.Count;
                Check(keysBefore == cfg.Alerts.Count(a => a.ShowOnMain) && !cfg.Alerts.Any(a => a.ShowOnMain && a.Name == "TWO-TONE TEST"), "Alert keys must be the alerts with ShowOnMain (test alerts hidden by default).");
                var toneSet = AlertProfile.NewToneSet(); toneSet.Name = "STATION 1"; toneSet.FrequencyA = 1153.4; toneSet.FrequencyB = 1251.4; toneSet.ColorHex = "#2050A0"; cfg.Alerts.Add(toneSet);
                cfg.Validate(); Call(window, "RefreshSelected");
                var toneKeys = (FlowLayoutPanel)Field(window, "_alertButtons");
                Check(toneKeys.Controls.Count == keysBefore + 1 && toneKeys.Controls.Cast<Control>().Any(c => c.Text == "STATION 1"), "A tone set with ShowOnMain must get a key on the main screen.");
                toneSet.ShowOnMain = false; Call(window, "RefreshSelected"); Check(((FlowLayoutPanel)Field(window, "_alertButtons")).Controls.Count == keysBefore, "Unticking ShowOnMain must remove the key.");
                cfg.Alerts.Remove(toneSet); Call(window, "RefreshSelected");
                var menus = window.MainMenuStrip.Items.OfType<ToolStripMenuItem>().ToList(); var settingsMenu = menus.First(m => m.Text == "Settings");
                var modeMenu = settingsMenu.DropDownItems.OfType<ToolStripMenuItem>().FirstOrDefault(m => m.Text == "Operating mode");
                Check(modeMenu != null && modeMenu.DropDownItems.Count == 3 && modeMenu.DropDownItems.OfType<ToolStripMenuItem>().Count(m => m.Checked) == 1 && settingsMenu.DropDownItems.OfType<ToolStripMenuItem>().Any(m => m.Text == "Engineering"), "Settings must hold Operating mode (one ticked) and Engineering.");
                Check(!menus.First(m => m.Text == "Tools").DropDownItems.OfType<ToolStripMenuItem>().Any(m => m.Text.StartsWith("SB9600") || m.Text.StartsWith("Diagnostics")), "Engineering tools must not be in the Tools menu.");
                setup.RequireArm = false; Call(window, "BuildUi"); Application.DoEvents(); Check(!((Control)Field(window, "_arm")).Visible, "ARM TX must be hidden when RequireArm is off.");
                setup.RequireArm = true; Call(window, "BuildUi"); Application.DoEvents(); Check(((Control)Field(window, "_arm")).Visible, "ARM TX must show when RequireArm is on.");
                var tools = (Dictionary<string, Form>)Field(window, "_tools");
                Check(tools.Count == 8 && tools.ContainsKey("HEAD") && tools.ContainsKey("DTMF") && tools.Values.All(f => f.KeyPreview), "Tool windows missing or not forwarding hotkeys (" + tools.Count + ").");
                Check(((Control)Field(window, "_ptt")).BackColor == ConsoleWindow.PttIdle, "HOLD TO TALK must be bright red.");
                Call(window, "ShowTool", "HEAD"); Application.DoEvents(); Check(tools["HEAD"].Visible && ((Control)Field(window, "_w7Surface")).Visible, "W7 control head window did not open."); tools["HEAD"].Hide();
                // quick keys light like the radio-function buttons did
                // (the menu test above left MON on: start from off so func:MON turns it on)
                states[radio.Id].Monitor = false; Call(window, "RefreshDisplay");
                Call(window, "RunAction", "func:MON", "test"); Pump(window, 100);
                int monIndex = cfg.QuickKeys.FindIndex(q => q.Action == "func:MON" || q.Action == "trc:MON");
                Check(monIndex >= 0 && states[radio.Id].Monitor && ((FlowLayoutPanel)Field(window, "_quick")).Controls[monIndex].BackColor == ((UiTheme)Field(window, "_t")).Active, "MONITOR quick key does not light while monitor is on.");
                if (states[radio.Id].Monitor) { Call(window, "RunAction", "func:MON", "test"); Pump(window, 100); }
                // the W7 head can still be put back on the main screen
                cfg.Ui.ShowHeadPanel = true; Call(window, "BuildUi"); Application.DoEvents();
                Check(((Control)Field(window, "_w7Surface")).Visible && !((Dictionary<string, Form>)Field(window, "_tools")).ContainsKey("HEAD"), "ShowHeadPanel did not put the W7 head on the main screen.");
                cfg.Ui.ShowHeadPanel = false; Call(window, "BuildUi"); Application.DoEvents();
                tools = (Dictionary<string, Form>)Field(window, "_tools");
                Call(window, "ShowTool", "QUEUE"); Application.DoEvents(); Check(tools["QUEUE"].Visible, "Queue did not open.");
                tools["QUEUE"].Close(); Application.DoEvents(); Check(!tools["QUEUE"].IsDisposed && !tools["QUEUE"].Visible, "Closing a tool window must hide it, not destroy it.");
                radio.Bus.W7Top1Label = "SECURE"; radio.Bus.W7Top1Binding = "TOP4"; Call(window, "RefreshSelected");
                var topKeys = (List<Button>)Field(window, "_w7TopKeys");
                Check(topKeys[0].Text == "SECURE" && (string)topKeys[0].Tag == "TOP4", "W7 top-key mapping failed.");
                radio.Bus.W7Top1Label = ""; radio.Bus.W7Top1Binding = ""; Call(window, "RefreshSelected");
                Control brand = (Control)Field(window, "_brand");
                Check(brand.Height >= brand.Font.Height && brand.Parent.ClientRectangle.Contains(brand.Bounds), "Header title clips its text.");

                // ---- every on-screen shortcut works from the keyboard, and does the same thing ----
                Check(((List<string>)Call(window, "HotkeyProblems")).Count == 0, "Default hotkeys report problems: " + String.Join("; ", ((List<string>)Call(window, "HotkeyProblems")).ToArray()));
                var quickBar = (FlowLayoutPanel)Field(window, "_quick");
                int captioned = 0;
                foreach (Control c in quickBar.Controls)
                    if (c is Button && c.Tag is Keys && (Keys)c.Tag != Keys.None) { string[] lines = c.Text.Split('\n'); Check(Hotkey.Parse(lines[lines.Length - 1]) == (Keys)c.Tag, "Quick key caption does not match its key: " + c.Text); captioned++; }
                Check(captioned == cfg.QuickKeys.Count(q => Hotkey.Parse(q.Hotkey) != Keys.None) && captioned >= 8, "Quick keys missing their hotkey captions.");
                for (int qi = 0; qi < cfg.QuickKeys.Count; qi++)
                {
                    QuickKey q = cfg.QuickKeys[qi]; if (!q.Action.StartsWith("chan:")) continue;
                    radio.SelectedChannel = 7; Check((bool)Call(window, "HandleHotkey", Hotkey.Parse(q.Hotkey), false), "Hotkey " + q.Hotkey + " not handled."); int byKey = radio.SelectedChannel;
                    radio.SelectedChannel = 7; Button qb = quickBar.Controls.OfType<Button>().First(b => b.Name == "QK" + qi); qb.PerformClick(); int byClick = radio.SelectedChannel;
                    Check(byKey == byClick && byKey != 7, "Quick key " + q.Label + ": keyboard " + q.Hotkey + " selected " + byKey + " but the button selected " + byClick + ".");
                }
                HotkeySettings keys = cfg.Hotkeys;
                radio.SelectedChannel = 0; Call(window, "HandleHotkey", Hotkey.Parse(keys.ChannelUp), false); int up = radio.SelectedChannel;
                radio.SelectedChannel = 0; FindButton(window, "CH +").PerformClick(); Check(up == 1 && radio.SelectedChannel == 1, "CH + key and button disagree.");
                Call(window, "HandleHotkey", Hotkey.Parse(keys.ChannelDown), false); Check(radio.SelectedChannel == 0, "CH - key failed.");
                FindButton(window, "CH -").PerformClick(); Check(radio.SelectedChannel == radio.Channels.Count - 1, "CH - button failed.");
                Check(FindButton(window, "CH +").Text.EndsWith(Hotkey.Display(keys.ChannelUp)) && FindButton(window, "HOLD TO TALK").Text.EndsWith(Hotkey.Display(keys.Ptt)) && FindButton(window, "STOP ALL").Text.EndsWith(Hotkey.Display(keys.StopAll).ToUpperInvariant()), "Button captions must print the bound key.");
                Check((FindButton(window, "RADIO >") != null) == (cfg.Radios.Count > 1) && (FindButton(window, "< RADIO") != null) == (cfg.Radios.Count > 1) && FindButton(window, "REPLAY") != null && FindButton(window, "MONITOR\n") != null, "Hotkey buttons missing (RADIO < > only with more than one radio).");
                Call(window, "HandleHotkey", Hotkey.Parse(keys.Ptt), false); Check(engine.Active, "PTT hotkey did not key.");
                Call(window, "HandleHotkey", Hotkey.Parse(keys.Ptt), true); Check(engine.Active, "Key repeat must not re-key.");
                Call(window, "HandleHotkeyUp", Hotkey.Parse(keys.Ptt) & Keys.KeyCode); Check(!engine.Active, "Releasing the PTT hotkey did not unkey.");
                Call(window, "HandleHotkey", Hotkey.Parse(keys.Ptt), false); Call(window, "HandleHotkey", Hotkey.Parse(keys.StopAll), false); Check(!engine.Active && (Keys)Field(window, "_heldPttKey") == Keys.None, "STOP ALL hotkey failed.");
                // the key reaches the console through ProcessCmdKey even when a text box, combo box or tool window has focus
                // DTMF digits live in their own window (Tools > DTMF): typing there must not trigger shortcuts, Ctrl+2 still works
                Form dtmfTool = tools["DTMF"]; dtmfTool.Show(window); Control digits = (Control)Field(window, "_digits"); dtmfTool.ActiveControl = digits; Application.DoEvents();
                Check(digits.FindForm() == dtmfTool, "DTMF must be in its own window, not on the main screen.");
                radio.SelectedChannel = 0; Message pgUp = Message.Create(dtmfTool.Handle, 0x100, (IntPtr)(int)Keys.PageUp, IntPtr.Zero);
                Call(dtmfTool, "ProcessCmdKey", pgUp, Keys.PageUp); Check(radio.SelectedChannel == 0, "Plain keys must type into a text box, not trigger shortcuts.");
                Message ctrl2 = Message.Create(dtmfTool.Handle, 0x100, (IntPtr)(int)Keys.D2, IntPtr.Zero);
                Check((bool)Call(dtmfTool, "ProcessCmdKey", ctrl2, Keys.Control | Keys.D2) && radio.SelectedChannel == 1, "Ctrl+2 must work while typing.");
                dtmfTool.Hide(); window.Activate(); Application.DoEvents();
                Message down = Message.Create(window.Handle, 0x100, (IntPtr)(int)Keys.F12, IntPtr.Zero);
                Check((bool)Call(window, "ProcessCmdKey", down, Keys.F12) && engine.Active, "F12 through ProcessCmdKey did not key."); Call(window, "HandleHotkeyUp", Keys.F12); Check(!engine.Active, "F12 release failed.");
                Form mixer = tools["MIXER"]; mixer.Show(window); Application.DoEvents();
                Message toolDown = Message.Create(mixer.Handle, 0x100, (IntPtr)(int)Keys.F12, IntPtr.Zero);
                Check((bool)Call(mixer, "ProcessCmdKey", toolDown, Keys.F12) && engine.Active, "PTT hotkey must work while a tool window has focus."); Call(window, "HandleHotkeyUp", Keys.F12); Check(!engine.Active, "Tool-window PTT release failed."); mixer.Hide();
                window.ActiveControl = null;
                cfg.QuickKeys.Add(new QuickKey("CLASH", "trc:MON", "", "F12")); Check(((List<string>)Call(window, "HotkeyProblems")).Count == 1, "Hotkey conflict not reported."); cfg.QuickKeys.RemoveAt(cfg.QuickKeys.Count - 1);
                cfg.QuickKeys.Add(new QuickKey("BAD", "trc:MON", "", "Ctrl+Banana")); Check(((List<string>)Call(window, "HotkeyProblems")).Count == 1, "Unknown hotkey not reported."); cfg.QuickKeys.RemoveAt(cfg.QuickKeys.Count - 1);

                // ---- SB9600 first, TRC tone as the fail-safe ----
                Check(radio.Bus.PreferBusCommands && (bool)Call(window, "BusUsable", radio), "SB9600 must be the primary command path when the bus is usable.");
                var migrateMon = new DispatchConfig(); migrateMon.QuickKeys.Add(new QuickKey("MONITOR", "trc:MON", "", "F4")); migrateMon.Normalize();
                Check(migrateMon.QuickKeys[0].Action == "func:MON", "The seeded MONITOR quick key must use the SB9600-first monitor.");
                var v5 = new DispatchConfig { Version = 5 }; v5.Hotkeys.Monitor = "F9"; v5.QuickKeys.Add(new QuickKey("MONITOR", "func:MON", "", "F4")); v5.QuickKeys.Add(new QuickKey("CH 1", "chan:1", "", "Ctrl+1")); v5.Normalize();
                Check(v5.Version == 8 && v5.Recorder.DiskLogging && v5.QuickKeys.Count == 4 && v5.QuickKeys[1].Action == "func:SCAN" && v5.QuickKeys[1].Hotkey == "F5" && v5.QuickKeys[2].Action == "func:NUIS DEL" && v5.Hotkeys.Monitor == "", "v0.8.0 settings must gain SCAN / NUIS DEL quick keys next to MONITOR, and MON must lose its duplicate F9 key.");
                v5.Normalize(); Check(v5.QuickKeys.Count == 4, "Function quick keys added twice.");
                // ---- indicators follow the radio's own reports (bench logs) ----
                RadioState ist = states[radio.Id];
                Call(window, "ApplySbep", radio, ist, Sbep.Encode(0x21, new byte[] { 1, 0x02, 1 })); Check(ist.Scan && ist.Known.Contains("SCAN"), "SCAN light (indicator 0x02) did not follow the radio.");
                Call(window, "ApplySbep", radio, ist, Sbep.Encode(0x21, new byte[] { 1, 0xFF, 0 })); Check(!ist.Scan, "ALL = off must clear the SCAN light.");
                Call(window, "ApplyFrame", radio, ist, new SbFrame(0x01, 0x05, 0x01, 0x3C)); Check(ist.Monitor && ist.Known.Contains("MON"), "MON light did not follow DISPLY field 0x05.");
                Call(window, "ApplySbep", radio, ist, Sbep.DisplayMessage(0, 0, "MON OFF ")); Check(!ist.Monitor, "MON light did not follow the MON OFF display.");
                radio.Bus.IndicatorDirect = 0x06; Call(window, "ApplySbep", radio, ist, Sbep.Encode(0x21, new byte[] { 1, 0x06, 1 })); Check(ist.Direct && ist.Known.Contains("DIR"), "Mapped DIR indicator did not apply.");
                radio.Bus.IndicatorDirect = -1; ist.Direct = false; ist.Monitor = false; ist.Scan = false;
                // scan landing: the display shows another channel; the keys stay on the operator's channel, the top display shows it
                radio.SelectedChannel = 0; ist.Scan = true;
                Call(window, "ApplySbep", radio, ist, Sbep.DisplayMessage(0, 0, radio.Channels[2].Name.PadRight(24)));
                Check(radio.SelectedChannel == 0 && ist.RxChannel == 2 && ((LcdPanel)Field(window, "_lcd")).Main == radio.Channels[2].Name, "A scan landing must only change the top display, not the selected channel (selected " + radio.SelectedChannel + ", rx " + ist.RxChannel + ").");
                Check(((string)Call(window, "ChannelLabel", radio.Id, "RX", DateTime.Now)).StartsWith(radio.Channels[2].Name) && ((string)Call(window, "ChannelLabel", radio.Id, "TX", DateTime.Now)).StartsWith(radio.Channels[0].Name), "Recordings must be labelled with the receive channel (RX) and the operator's channel (TX).");
                Call(window, "ApplySbep", radio, ist, Sbep.DisplayMessage(0, 0, radio.Channels[0].Name.PadRight(24)));
                Check(radio.SelectedChannel == 0 && ist.RxChannel == -1, "The top display must return to the operator's channel when the radio does.");
                // start-up sync: the console takes the channel the radio display names, once
                ist.SyncAdopt = true; Call(window, "ApplySbep", radio, ist, Sbep.DisplayMessage(0, 0, "VOL    9".PadRight(24)));
                Check(radio.SelectedChannel == 0 && ist.SyncAdopt, "A VOL pop-up must not change the channel.");
                Call(window, "ApplySbep", radio, ist, Sbep.DisplayMessage(0, 0, radio.Channels[3].Name.PadRight(24)));
                Check(radio.SelectedChannel == 3 && !ist.SyncAdopt && ist.RxChannel == -1, "Sync must take the channel the radio shows (selected " + radio.SelectedChannel + ").");
                radio.SelectedChannel = 0; Call(window, "ApplySbep", radio, ist, Sbep.DisplayMessage(0, 0, radio.Channels[0].Name.PadRight(24)));
                ist.Scan = false;
                // DIR from the channel until the radio reports it (simplex = DIR)
                radio.Bus.IndicatorDirect = 0x06; radio.Channels[0].RxMHz = 155.1; radio.Channels[0].TxMHz = 155.1; ist.Known.Remove("DIR");
                ConsoleWindow.InferDirect(radio, ist); Check(ist.Direct && ist.Known.Contains("DIR"), "DIR must be inferred for a simplex channel.");
                radio.Bus.IndicatorDirect = -1; radio.Channels[0].RxMHz = 0; radio.Channels[0].TxMHz = 0; ist.Direct = false; ist.Known.Remove("DIR");
                Check((string)Call(window, "ResolveFunctionKey", "SCAN", "SCAN") == "TOP3", "SCAN must press W7 top key 3 when no head button is named SCAN.");
                // ---- command verification: confirmed when the radio answers, NOT CONFIRMED when it stays silent ----
                Type ccType = typeof(ConsoleWindow).GetNestedType("CmdCheck", BindingFlags.NonPublic);
                var checks = (System.Collections.IList)Field(window, "_checks");
                Func<string, string, int, object> mk = (what, kind, ms) =>
                {
                    object c = Activator.CreateInstance(ccType);
                    ccType.GetField("RadioId").SetValue(c, radio.Id); ccType.GetField("What").SetValue(c, what); ccType.GetField("Kind").SetValue(c, kind);
                    ccType.GetField("Seq").SetValue(c, states[radio.Id].BeepSeq + states[radio.Id].DisplaySeq + states[radio.Id].IndSeq);
                    ccType.GetField("Deadline").SetValue(c, DateTime.UtcNow.AddMilliseconds(ms)); return c;
                };
                checks.Add(mk("NUIS DEL", "KEY", 1000)); states[radio.Id].BeepSeq++; Call(window, "ProcessChecks");
                Check(checks.Count == 0 && (string)Field(window, "_lastCheck") == "NUIS DEL CONFIRMED", "A key beep from the radio must confirm the command.");
                checks.Add(mk("SEL", "KEY", -1)); Call(window, "ProcessChecks");
                Check(checks.Count == 0 && (string)Field(window, "_lastCheck") == "SEL NOT CONFIRMED", "Silence from the radio must be reported as NOT CONFIRMED.");
                // ---- live transmit gates (v0.6.1 safety) ----
                FieldInfo modeField = typeof(ConsoleWindow).GetField("_opMode", BindingFlags.NonPublic | BindingFlags.Instance);
                modeField.SetValue(window, ConsoleWindow.OpMode.Live);
                radio.SelectedChannel = 0; radio.Channels[0].RxOnly = true; Check(!(bool)Call(window, "ChannelCanTransmit", radio), "Receive-only channel must not transmit.");
                radio.Channels[0].RxOnly = false; int savedHz = radio.Channels[0].FunctionHz; radio.Channels[0].FunctionHz = 0; Check(!(bool)Call(window, "ChannelCanTransmit", radio), "Channel without a TRC tone must not transmit.");
                radio.Channels[0].FunctionHz = savedHz;
                // the radio keys, then drops while PTT is held: the console must say so instead of showing ON AIR
                radio.Ptt = PttMethod.TrcTones; Call(window, "StartTxState", new List<string> { radio.Id }, "PTT", 0);
                states[radio.Id].BusTx = true; Call(window, "MonitorLinks");
                states[radio.Id].BusTx = false; Call(window, "MonitorLinks"); System.Threading.Thread.Sleep(450); Call(window, "MonitorLinks");
                Check(states[radio.Id].RadioDropped, "A radio that stops transmitting while PTT is held must be flagged.");
                states[radio.Id].BusTx = true; Call(window, "MonitorLinks"); Check(!states[radio.Id].RadioDropped, "The flag must clear when the radio transmits again.");
                Call(window, "StopTx", "test"); states[radio.Id].BusTx = false;
                modeField.SetValue(window, ConsoleWindow.OpMode.Simulation);
                // zoned codeplug: closed-loop channel seek over the simulated bus, and RX-only TX block
                string cpsFile = Path.Combine("cps", "Example_XTL5000_Channels.csv");
                if (File.Exists(cpsFile))
                {
                    RadioProfile xtl = cfg.Radios[0];
                    xtl.Channels.Clear(); xtl.Channels.AddRange(CpsImport.ToChannels(CpsImport.Parse(File.ReadAllLines(cpsFile)), CpsImport.LoadNames("cps"), new HashSet<int> { 3, 6 }, true));
                    xtl.Bus.HeadButtons.Add(new HeadButton("ZONE UP", 0x90, "test")); xtl.Bus.HeadButtons.Add(new HeadButton("ZONE DN", 0x91, "test"));
                    xtl.ChannelSelect = ChannelMethod.Sb9600ModeKeys; xtl.HomeChannel = 1; xtl.SelectedChannel = 0;
                    Call(window, "OpenLinksForMode"); Call(window, "RebuildCards"); Call(window, "RefreshSelected");
                    int target = xtl.Channels.FindIndex(c => c.ZoneNumber == 3 && c.ZonePosition == 9);
                    Call(window, "ChangeChannel", target); Pump(window, 9000);
                    Check(xtl.SelectedChannel == target && ((System.Collections.IDictionary)Field(window, "_seeks")).Count == 0 && states[xtl.Id].BusDisplay.Trim() == "LAW I/O", "Closed-loop zone/channel seek did not reach Z3/9 LAW I/O (display '" + states[xtl.Id].BusDisplay.Trim() + "').");
                    Check(xtl.Channels[target].BusMode == target + 1 && xtl.Bus.GotoOffsetKnown && xtl.Bus.GotoOffset == 1 && !states[xtl.Id].DirectGotoFailed, "Direct channel select was not used / learned (one command instead of stepping through channels).");
                    // fallback: with direct select off, the ZONE / MODE keys still get there, one confirmed press at a time
                    xtl.Bus.DirectChannelGoto = false; int z5 = xtl.Channels.FindIndex(c => c.ZoneNumber == 5 && c.ZonePosition == 13);
                    Call(window, "ChangeChannel", z5); Pump(window, 12000);
                    Check(xtl.SelectedChannel == z5 && ((System.Collections.IDictionary)Field(window, "_seeks")).Count == 0 && states[xtl.Id].BusDisplay.Trim() == "SO SEC", "Key-stepping fallback did not reach Z5/13 (display '" + states[xtl.Id].BusDisplay.Trim() + "').");
                    xtl.Bus.DirectChannelGoto = true;
                    // duplicate label: Z5/13 -> Z1/3 FIRE MA, then to the other FIRE MA (Z3/1); the radio must really be on each
                    int fireMa1 = xtl.Channels.FindIndex(c => c.ZoneNumber == 1 && c.ZonePosition == 3), fireMa3 = xtl.Channels.FindIndex(c => c.ZoneNumber == 3 && c.ZonePosition == 1);
                    Call(window, "ChangeChannel", fireMa1); Pump(window, 3000); Check(xtl.SelectedChannel == fireMa1 && states[xtl.Id].BusChannel == fireMa1 + 1, "Direct select to Z1/3 FIRE MA failed.");
                    Call(window, "ChangeChannel", fireMa3); Pump(window, 3000); Check(xtl.SelectedChannel == fireMa3 && states[xtl.Id].BusChannel == fireMa3 + 1, "Direct select to the other FIRE MA (Z3/1) failed.");
                    xtl.SelectedChannel = xtl.Channels.FindIndex(c => c.RadioText == "MA RED");
                    Call(window, "StartPtt", "SELECTED"); Check(!engine.Active, "RX-only channel must not be keyed.");
                    Call(window, "RunAction", "chan:CITY FD", "test"); Pump(window, 6000);
                    Check(xtl.Channels[xtl.SelectedChannel].RadioText == "CITY FD" && states[xtl.Id].BusDisplay.Trim() == "CITY FD", "chan:CITY FD quick-key action did not reach CITY FD.");
                    Call(window, "HeadKey", "HOME"); Pump(window, 4000); Check(xtl.SelectedChannel == 1 && ((System.Collections.IDictionary)Field(window, "_seeks")).Count == 0 && states[xtl.Id].BusDisplay.Trim() == "STATE FD", "HOME must select the home channel (Z1/2 STATE FD).");
                }
                if (Environment.GetEnvironmentVariable("DISPATCH_UI_HOLD") == "1") Pump(window, 6000);
                window.Close();
            }
            // ---- a settings file that cannot be read never stops the console: the .bak copy is used ----
            string damagedDir = Path.Combine(temp, "damaged"); Directory.CreateDirectory(damagedDir);
            string damaged = Path.Combine(damagedDir, AppPaths.ConfigName);
            var good = DispatchConfig.Load(damaged); good.ConsoleName = "BACKUP COPY"; good.Save(damaged); good.Save(damaged);   // second save leaves the first as .bak
            File.WriteAllText(damaged, "<DispatchConfig><broken");
            using (var recovered = new ConsoleWindow(damaged, true))
            {
                ((System.Windows.Forms.Timer)Field(recovered, "_tick")).Stop();
                var rc = (DispatchConfig)Field(recovered, "_config");
                Check(rc.ConsoleName == "BACKUP COPY" && rc.SeedNote.Contains("could not be read") && Directory.GetFiles(damagedDir, AppPaths.ConfigName + ".unreadable-*").Length == 1, "An unreadable settings file must fall back to the .bak copy and be kept aside.");
            }
        }
    }
}
