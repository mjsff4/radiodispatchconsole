using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO.Ports;
using System.Text;
using System.Threading;

// SB9600 / SBEP bus access through a RIB (RS-232 <-> SB9600 bridge, e.g. RLN4008/PLN4008 style).
//
// Sources for every protocol fact used here (nothing else is assumed):
//   * Motorola SB9600 Serial Bus Protocol Manual v05.13 (public copy on wiki.w9cr.net):
//       9600 baud 8N1, 5-byte messages (address, data, data, opcode, CRC), BUSY arbitration.
//   * RadioConsole2 daemon (W3AXL, SB9600.cs) - working XTL5000 / Astro W9 implementation:
//       W-series head button codes, BUTCTL value 0/1/2 (up/down/toggle), RADKEY / SQLDET / ACTMDU
//       status opcodes, SBEP framing (header nibble opcode + length, sum checksum), SBEP display
//       (opcode 0x01) and indicator (0x21) layouts, BUSY on DTR / sensed on CTS, BUSY self-test, reset 00 00 01 08.
//   * XTL-SB9600-Playground (W3AXL, building on Paul Banks' SB9600 work): CRC parameters + test vectors, echo
//       verification, O5 codes. (The CRC table here is computed, not copied.)
// The W7 head is a W-series head; its codes are expected to match the W9 table, but verify with LEARN.
namespace RadioDispatchConsole
{
    internal static class SbCrc
    {
        // SB9600 CRC-8, reflected polynomial 0xCF (initial value 0). The table is computed at start-up; the self-test
        // checks it against published SB9600 frames.
        private static readonly byte[] Table = BuildTable();
        private static byte[] BuildTable()
        {
            var table = new byte[256];
            for (int n = 0; n < 256; n++)
            {
                int c = n;
                for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? (c >> 1) ^ 0xCF : c >> 1;
                table[n] = (byte)c;
            }
            return table;
        }
        public static byte Compute(byte[] data, int start, int count)
        {
            int crc = 0;
            for (int i = 0; i < count; i++) crc = Table[(crc ^ data[start + i]) & 0xFF];
            return (byte)crc;
        }
        // SBEP checksum: sum of all preceding bytes, then XOR 0xFF.
        public static byte Sbep(byte[] data, int start, int count)
        {
            int sum = 0;
            for (int i = 0; i < count; i++) sum = (sum + data[start + i]) & 0xFF;
            return (byte)(sum ^ 0xFF);
        }
    }

    internal sealed class SbFrame
    {
        public byte Addr, P1, P2, Func, Crc;
        public DateTime Time;
        public bool Transmitted;
        public SbFrame(byte addr, byte p1, byte p2, byte func)
        {
            Addr = addr; P1 = p1; P2 = p2; Func = func; Time = DateTime.Now;
            Crc = SbCrc.Compute(new byte[] { addr, p1, p2, func }, 0, 4);
        }
        public SbFrame(byte[] five)
        {
            Addr = five[0]; P1 = five[1]; P2 = five[2]; Func = five[3]; Crc = five[4]; Time = DateTime.Now;
        }
        public byte[] Bytes() { return new byte[] { Addr, P1, P2, Func, Crc }; }
        public byte[] Four() { return new byte[] { Addr, P1, P2, Func }; }
        public bool CrcOk { get { return SbCrc.Compute(Four(), 0, 4) == Crc; } }
        public override string ToString()
        {
            return Addr.ToString("X2") + " " + P1.ToString("X2") + " " + P2.ToString("X2") + " " + Func.ToString("X2") + " " + Crc.ToString("X2");
        }
    }

    internal static class SbHex
    {
        public static bool TryParse(string text, out byte[] bytes)
        {
            bytes = null; if (text == null) return false;
            string[] parts = text.Split(new char[] { ' ', ',', '-', ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > 64) return false;
            var list = new List<byte>();
            foreach (string part in parts)
            {
                string p = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part.Substring(2) : part;
                int n; if (p.Length == 0 || p.Length > 2 || !Int32.TryParse(p, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out n)) return false;
                list.Add((byte)n);
            }
            bytes = list.ToArray(); return true;
        }
        public static string ToHex(byte[] data, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && i < data.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("X2")); }
            return sb.ToString();
        }
        // Accepts 4 bytes (CRC appended) or 5 bytes (CRC checked).
        public static bool TryFrame(string text, out SbFrame frame, out string error)
        {
            frame = null; error = null; byte[] b;
            if (!TryParse(text, out b)) { error = "Use hex bytes such as 05 61 01 57"; return false; }
            if (b.Length == 4) { frame = new SbFrame(b[0], b[1], b[2], b[3]); return true; }
            if (b.Length == 5)
            {
                frame = new SbFrame(b);
                if (!frame.CrcOk) { error = "CRC byte should be " + SbCrc.Compute(b, 0, 4).ToString("X2"); frame = null; return false; }
                return true;
            }
            error = "A frame is 4 bytes (CRC added) or 5 bytes (CRC checked)."; return false;
        }
    }

    internal static class SbPattern
    {
        public static bool TryParse(string text, out byte[] value, out byte[] mask)
        {
            value = null; mask = null; if (text == null) return false;
            string[] parts = text.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return false;
            value = new byte[4]; mask = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                if (parts[i] == "**" || parts[i] == "*") { mask[i] = 0; continue; }
                int n; if (parts[i].Length > 2 || !Int32.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out n)) return false;
                value[i] = (byte)n; mask[i] = 0xFF;
            }
            return true;
        }
        public static bool Matches(SbFrame f, string pattern)
        {
            byte[] v, m; if (!TryParse(pattern, out v, out m)) return false;
            byte[] four = f.Four();
            for (int i = 0; i < 4; i++) if ((four[i] & m[i]) != (v[i] & m[i])) return false;
            return true;
        }
        public static List<BusRule> Match(SbFrame f, List<BusRule> rules)
        {
            var hits = new List<BusRule>();
            if (rules == null) return hits;
            foreach (BusRule rule in rules) if (rule != null && rule.Enabled && Matches(f, rule.Pattern)) hits.Add(rule);
            return hits;
        }
    }

    // SBEP message framing (header: high nibble = opcode, 0xF = extended opcode byte follows;
    // low nibble = length incl. checksum, 0xF = two-byte big-endian length follows).
    internal static class Sbep
    {
        // Total message length once enough header bytes are present; 0 = need more bytes.
        public static int ExpectedLength(List<byte> b)
        {
            if (b.Count < 1) return 0;
            int msn = b[0] >> 4, lsn = b[0] & 0x0F, hdr = 1;
            if (msn == 0x0F) { if (b.Count < 2) return 0; hdr = 2; }
            int len;
            if (lsn == 0x0F) { if (b.Count < hdr + 2) return 0; len = (b[hdr] << 8) | b[hdr + 1]; hdr += 2; }
            else len = lsn;
            return hdr + Math.Max(len, 1);
        }
        public static bool Decode(byte[] m, out int opcode, out byte[] data)
        {
            opcode = -1; data = new byte[0];
            if (m == null || m.Length < 2) return false;
            int msn = m[0] >> 4, lsn = m[0] & 0x0F, hdr = 1;
            if (msn == 0x0F) { opcode = m[1]; hdr = 2; } else opcode = msn;
            int len;
            if (lsn == 0x0F) { if (m.Length < hdr + 2) return false; len = (m[hdr] << 8) | m[hdr + 1]; hdr += 2; } else len = lsn;
            if (len < 1 || m.Length < hdr + len) return false;
            data = new byte[len - 1]; Array.Copy(m, hdr, data, 0, len - 1);
            return SbCrc.Sbep(m, 0, hdr + len - 1) == m[hdr + len - 1];
        }
        public static byte[] Encode(int opcode, byte[] data)
        {
            var msg = new List<byte>(); int len = data.Length + 1; byte first = 0;
            if (opcode >= 0x0F) first |= 0xF0; else first |= (byte)(opcode << 4);
            if (len > 0x0E) first |= 0x0F; else first |= (byte)len;
            msg.Add(first);
            if (opcode >= 0x0F) msg.Add((byte)opcode);
            if (len > 0x0E) { msg.Add((byte)(len >> 8)); msg.Add((byte)(len & 0xFF)); }
            msg.AddRange(data);
            byte[] body = msg.ToArray(); msg.Add(SbCrc.Sbep(body, 0, body.Length));
            return msg.ToArray();
        }
        public static byte[] DisplayMessage(int row, int col, string text)
        {
            byte[] chars = Encoding.ASCII.GetBytes(text);
            var data = new byte[5 + chars.Length];
            data[0] = (byte)row; data[1] = (byte)col; data[2] = (byte)chars.Length; data[3] = (byte)row; data[4] = (byte)col;
            Array.Copy(chars, 0, data, 5, chars.Length);
            return Encode(0x01, data);
        }
    }

    internal sealed class SbInfo
    {
        public string Text = "", Category = "UNK", Name = "", Str = "";
        public int Code = -1, Value = -1, Row = 0;
        public byte[] Codes, States;
    }

    internal static class SbDecoder
    {
        private static readonly string[] Modules = { "BCAST", "RADIO", "DSP", "MPL", "INTOPT", "PANEL", "REARPNL", "EXTPNL", "SIREN", "SECURENET", "EMERG", "SELCALL", "MDC600", "MVS", "PHONE", "DTMF", "TRNKSYS", "TRNKOPT", "VRS", "SPRPT", "SINGLETONE" };
        public static string Module(int a) { return a < Modules.Length ? Modules[a] : "0x" + a.ToString("X2"); }

        // W-series (Astro W9) indicator codes from RadioConsole2. W7 assumed identical; verify.
        public static string IndicatorName(int code)
        {
            if (code >= 0x07 && code <= 0x0C) return "TOP" + (code - 0x06);
            if (code == 0x02) return "SCAN";   // W7 on the bench consolette: the SCAN key toggles it (bench log 20 Oct 2026)
            if (code == 0x0D) return "PRI"; if (code == 0x0E) return "NONPRI"; if (code == 0x0F) return "BUSY"; if (code == 0x10) return "TRANSMIT";
            return "0x" + code.ToString("X2");
        }

        public static SbInfo Describe(SbFrame f, List<HeadButton> buttons)
        {
            var info = new SbInfo(); string raw = f.ToString();
            if (f.Addr == 0x05 && f.Func == 0x57)
            {
                info.Category = "BTN"; info.Code = f.P1; info.Value = f.P2;
                string name = ButtonName(f.P1, buttons); info.Name = name;
                if (f.P1 == 0x02) info.Text = "Head volume knob " + f.P2;
                else info.Text = "Head " + name + (f.P2 == 1 ? " pressed" : f.P2 == 0 ? " released" : f.P2 == 2 ? " toggled" : " value " + f.P2);
            }
            else if (f.Addr == 0x05 && f.Func == 0x58) { info.Category = "LIGHT"; info.Text = "Illumination 0x" + f.P1.ToString("X2") + " = " + f.P2; }
            else if (f.Addr == 0x00 && f.Func == 0x06) { info.Category = "EPREQ"; info.Text = "Expanded protocol request (SBEP) to " + Module(f.P2); }
            else if (f.Addr == 0x00 && f.Func == 0x0A)
            {
                if (f.P1 == 0x03) { info.Category = "TX"; info.Value = f.P2; info.Text = "SETBUT transmit " + (f.P2 == 1 ? "ON" : "OFF"); }
                else if (f.P1 == 0x01) { info.Category = "MON"; info.Value = f.P2; info.Text = "SETBUT monitor " + (f.P2 == 1 ? "ON" : "OFF"); }
                else info.Text = "SETBUT register 0x" + f.P1.ToString("X2") + " = " + f.P2;
            }
            else if (f.Addr == 0x00 && f.Func == 0x3B) { info.Category = "PRUPST"; info.Text = "Power-up self test " + (f.P1 == 0 ? "OK" : "ERRORS 0x" + f.P1.ToString("X2")) + " from " + Module(f.P2 & 0x1F); }
            else if (f.Addr == 0x00 && f.Func == 0x08) { info.Category = "RESET"; info.Text = "Reset / device select " + f.P1.ToString("X2") + " " + f.P2.ToString("X2"); }
            else if (f.Addr == 0x01 && f.Func == 0x15) { info.Category = "RADRDY"; info.Text = "Radio ready"; }
            else if (f.Addr == 0x01 && f.Func == 0x19) { info.Category = "TX"; info.Value = f.P2 == 1 ? 1 : 0; info.Text = "RADKEY radio " + (f.P2 == 1 ? "transmitting" : "unkeyed"); }
            else if (f.Addr == 0x01 && f.Func == 0x1A) { info.Category = "RXAUD"; info.Text = "RX audio routing " + f.P1.ToString("X2") + " " + f.P2.ToString("X2"); }
            else if (f.Addr == 0x01 && f.Func == 0x1B) { info.Category = "TXAUD"; info.Text = "TX audio routing " + f.P1.ToString("X2") + " " + f.P2.ToString("X2"); }
            else if (f.Addr == 0x01 && f.Func == 0x1D) { info.Category = "AUDIO"; info.Value = f.P2; info.Text = "Audio " + (f.P2 == 1 ? "unmuted" : f.P2 == 0 ? "muted" : "state " + f.P2); }
            else if (f.Addr == 0x01 && f.Func == 0x1E)
            {
                info.Category = "SQL"; info.Value = f.P2;
                info.Text = "Squelch " + (f.P2 == 0 ? "closed (idle)" : f.P2 == 3 ? "OPEN - receiving" : f.P2 == 1 ? "activity while scanning" : "state 0x" + f.P2.ToString("X2"));
            }
            else if (f.Addr == 0x01 && f.Func == 0x1F) { info.Category = "MODE"; info.Value = f.P2; info.Code = f.P1; info.Text = "Active mode update: mode " + f.P2 + " (options 0x" + f.P1.ToString("X2") + ")"; }
            else if (f.Addr == 0x01 && f.Func == 0x23)
            {
                info.Category = "PL"; info.Value = f.P2;
                string[] pl = { "no PL, unqualified", "no PL, qualified", "valid PL, unqualified", "valid PL, qualified" };
                info.Text = "PL detect: " + (f.P2 < 4 ? pl[f.P2] : "0x" + f.P2.ToString("X2"));
            }
            // bench log: the W7 monitor key (keypad 8) toggles DISPLY field 0x05 (1 = monitor on, 0 = off).
            else if (f.Addr == 0x01 && f.Func == 0x3C && f.P1 == 0x05) { info.Category = "MON"; info.Value = f.P2 == 0 ? 0 : 1; info.Text = "Monitor " + (f.P2 == 0 ? "OFF" : "ON") + " (display field 0x05)"; }
            else if (f.Addr == 0x01 && f.Func == 0x3C) { info.Category = "DISPLY"; info.Text = "DISPLY field 0x" + f.P1.ToString("X2") + " = 0x" + f.P2.ToString("X2"); }
            else if (f.Addr == 0x01 && f.Func == 0x60) { info.Category = "X60"; info.Text = "XTL opcode 0x60 (undocumented) " + f.P1.ToString("X2") + " " + f.P2.ToString("X2"); }
            else info.Text = Module(f.Addr) + " opcode 0x" + f.Func.ToString("X2") + " data " + f.P1.ToString("X2") + " " + f.P2.ToString("X2");
            info.Text = info.Text + "   [" + raw + "]";
            return info;
        }

        public static SbInfo DescribeSbep(byte[] raw)
        {
            var info = new SbInfo { Category = "SBEP" };
            string hex = SbHex.ToHex(raw, 48) + (raw.Length > 48 ? " ..." : "");
            int op; byte[] d;
            bool ok = Sbep.Decode(raw, out op, out d);
            if (!ok) { info.Text = "SBEP message failed checksum/length (" + raw.Length + " bytes)   [" + hex + "]"; return info; }
            if (op == 0x01 && d.Length >= 5)
            {
                int chars = Math.Min(d[2], d.Length - 5);
                var sb = new StringBuilder(); for (int i = 0; i < chars; i++) { byte c = d[5 + i]; sb.Append(c >= 32 && c < 127 ? (char)c : ' '); }
                info.Category = "DISP"; info.Row = d[3]; info.Code = d[4]; info.Str = sb.ToString();
                info.Text = "SBEP display row " + d[3] + " col " + d[4] + ": '" + info.Str + "'   [" + hex + "]";
            }
            else if (op == 0x21 && d.Length >= 1)
            {
                int count = d[0]; if (1 + 2 * count > d.Length) count = (d.Length - 1) / 2;
                info.Category = "IND"; info.Codes = new byte[count]; info.States = new byte[count];
                var sb = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    info.Codes[i] = d[1 + i]; info.States[i] = d[1 + count + i];
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(info.Codes[i] == 0xFF ? "ALL" : IndicatorName(info.Codes[i])).Append('=').Append(info.States[i] == 0 ? "off" : info.States[i] == 1 ? "on" : "flash");
                }
                info.Text = "SBEP indicators: " + sb + "   [" + hex + "]";
            }
            else info.Text = "SBEP opcode 0x" + op.ToString("X2") + ", " + d.Length + " data bytes   [" + hex + "]";
            return info;
        }

        public static string ButtonName(int code, List<HeadButton> buttons)
        {
            if (buttons != null) foreach (HeadButton b in buttons) if (b != null && b.Code == code) return b.Name;
            return "0x" + code.ToString("X2");
        }
        public static int ButtonCode(string name, List<HeadButton> buttons)
        {
            if (buttons != null) foreach (HeadButton b in buttons) if (b != null && String.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b.Code;
            return -1;
        }
    }

    internal sealed class SbItem
    {
        public enum Kinds { Rx, Tx, Raw, Sbep, Info, Error }
        public Kinds Kind;
        public SbFrame Frame;
        public byte[] Raw;
        public string Text = "";
        public DateTime Time = DateTime.Now;
    }

    internal sealed class Sb9600Link : IDisposable
    {
        private SerialPort _port;
        private BusSettings _cfg;
        private readonly object _lock = new object();
        private readonly List<byte> _buf = new List<byte>();
        private readonly List<SbItem> _items = new List<SbItem>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private BlockingCollection<byte[]> _txQueue;
        private Thread _txThread;
        private long _lastByteMs, _sbepStartMs, _lastSbepEndMs = -10000;
        private bool _inSbep, _closing, _simulated;
        private byte[] _echo; private int _echoPos, _echoLen; private bool _echoBad;
        private readonly ManualResetEvent _echoDone = new ManualResetEvent(false);
        private int _simMode, _simModePending = -1; private long _simModeDue;
        public int SimModeLagMs = 600;
        public List<string> SimChannels = new List<string>();
        // Optional zone model for the simulator: zone number per SimChannels entry, plus learned zone-key codes.
        public List<int> SimZones = new List<int>();
        public int SimZoneUp = -1, SimZoneDown = -1, SimHome = 0;
        public Dictionary<int, int> SimZoneDirect = new Dictionary<int, int>();
        public long RxFrames, TxFrames, BadEchoes, SbepMessages;
        public volatile int TxFailStreak;   // consecutive frames the bus could not deliver; the console then uses TRC tones
        private long _lastTxFailMs = -100000;
        // Failing = 2+ frames in a row undelivered within the last 10 s. After 10 s the bus is tried again (self-healing).
        public bool Failing { get { return TxFailStreak >= 2 && _clock.ElapsedMilliseconds - _lastTxFailMs < 10000; } }
        public bool IsOpen { get { return _simulated || (_port != null && _port.IsOpen); } }
        public bool Simulated { get { return _simulated; } }
        public bool TransmitAllowed { get { return _cfg != null && (_cfg.AllowTransmit || _simulated); } }
        public string PortLabel { get { return _simulated ? "SIM BUS" : _cfg == null ? "" : _cfg.PortName; } }

        public string Open(BusSettings cfg)
        {
            Close();
            if (cfg == null || String.IsNullOrEmpty(cfg.PortName)) return "Choose the RIB COM port in the radio's SB9600 settings.";
            try
            {
                _cfg = cfg; _closing = false;
                _port = new SerialPort(cfg.PortName, cfg.Baud, Parity.None, 8, StopBits.One);
                _port.Handshake = Handshake.None; _port.DtrEnable = false; _port.RtsEnable = false;
                _port.ReadTimeout = 100; _port.WriteTimeout = 500; _port.ReceivedBytesThreshold = 1;
                _port.DataReceived += OnData;
                _port.Open();
                SetBusy(false);
                try { _port.DiscardInBuffer(); } catch (Exception) { }
                StartWorker();
                Post(SbItem.Kinds.Info, "Opened " + cfg.PortName + " at " + cfg.Baud + " 8N1. " + (cfg.AllowTransmit ? "Bus TX allowed." : "LISTEN ONLY (enable 'Allow transmit' to control the radio)."));
                if (cfg.AllowTransmit && cfg.TestBusyOnConnect && cfg.BusyIn != BusInLine.None && cfg.BusyOut != BusOutLine.None) Post(SbItem.Kinds.Info, BusyLoopbackTest());
                if (cfg.AllowTransmit && cfg.ResetOnConnect) { Send(0x00, 0x00, 0x01, 0x08); Post(SbItem.Kinds.Info, "Reset sent (radio restarts and re-sends its display)."); }
                return null;
            }
            catch (Exception ex)
            {
                string msg = "Could not open " + cfg.PortName + ": " + ex.Message;
                try { Close(); } catch (Exception) { }
                return msg;
            }
        }

        // The RIB loops BUSY back: asserting our BUSY output should be seen on the BUSY input.
        public string BusyLoopbackTest()
        {
            if (_port == null || !_port.IsOpen) return "BUSY test: port closed.";
            if (BusyActive()) return "BUSY test skipped: bus is busy right now.";
            SetBusy(true); Thread.Sleep(5); bool high = BusyActive();
            SetBusy(false); Thread.Sleep(5); bool low = !BusyActive();
            if (high && low) return "BUSY self-test PASSED (" + _cfg.BusyOut + " out -> " + _cfg.BusyIn + " in).";
            return "BUSY self-test FAILED (asserted read " + (high ? "busy" : "idle") + ", released read " + (low ? "idle" : "busy") + "). Check the RIB cable, 9 V supply, line choice and 'invert' options.";
        }

        public void OpenSimulated(BusSettings cfg)
        {
            Close(); _cfg = cfg; _simulated = true; _closing = false; _simMode = 0; _simModePending = -1; StartWorker();
            Post(SbItem.Kinds.Info, "Simulated SB9600 bus: sent frames get replies modelled on XTL traffic. No serial port is used.");
        }

        private void StartWorker()
        {
            _txQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), 64);
            _txThread = new Thread(TxLoop); _txThread.IsBackground = true; _txThread.Name = "SB9600 TX"; _txThread.Start();
        }

        public void Close()
        {
            _closing = true;
            try { if (_txQueue != null) _txQueue.CompleteAdding(); } catch (Exception) { }
            _echoDone.Set();
            try { if (_txThread != null && _txThread.IsAlive) _txThread.Join(2000); } catch (Exception) { }
            _txThread = null;
            if (_port != null)
            {
                try { _port.DataReceived -= OnData; } catch (Exception) { }
                try { if (_port.IsOpen) { SetBusy(false); _port.Close(); } } catch (Exception) { }
                try { _port.Dispose(); } catch (Exception) { }
                _port = null;
            }
            _simulated = false; _txQueue = null;
            lock (_lock) { _buf.Clear(); _inSbep = false; }
        }

        public void Dispose() { Close(); }

        private void Post(SbItem.Kinds kind, string text) { PostItem(new SbItem { Kind = kind, Text = text }); }
        private void PostItem(SbItem item) { lock (_lock) { AddItemLocked(item); } }
        private void AddItemLocked(SbItem item) { _items.Add(item); if (_items.Count > 6000) _items.RemoveRange(0, 1000); }
        public List<SbItem> Drain()
        {
            lock (_lock)
            {
                if (_simulated && _simModePending >= 0 && _clock.ElapsedMilliseconds >= _simModeDue)
                {
                    int m = _simModePending; _simModePending = -1; SimFrame(0x01, 0x00, (byte)m, 0x1F);
                }
                FlushIdleLocked();
                if (_items.Count == 0) return new List<SbItem>();
                var copy = new List<SbItem>(_items); _items.Clear(); return copy;
            }
        }

        // ---------------- receive ----------------
        private void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                SerialPort p = _port; if (p == null || !p.IsOpen) return;
                int n = p.BytesToRead; if (n <= 0) return;
                var data = new byte[n]; int got = p.Read(data, 0, n);
                Feed(data, got);
            }
            catch (Exception) { }
        }

        // Public so tests and the simulator can push bytes through the real parser.
        public void Feed(byte[] data, int count)
        {
            lock (_lock)
            {
                long now = _clock.ElapsedMilliseconds;
                for (int i = 0; i < count; i++)
                {
                    byte b = data[i];
                    if (_echoLen > 0 && _echoPos < _echoLen)
                    {
                        if (b == _echo[_echoPos]) { _echoPos++; if (_echoPos == _echoLen) _echoDone.Set(); continue; }
                        _echoBad = true; _echoDone.Set();
                    }
                    AddByteLocked(b, now);
                }
            }
        }

        private void AddByteLocked(byte b, long now)
        {
            _lastByteMs = now;
            if (_inSbep)
            {
                if (_buf.Count == 0 && b == 0x50) return;           // leading SBEP ACK
                _buf.Add(b);
                int need = Sbep.ExpectedLength(_buf);
                bool badHeader = _buf.Count == 1 && (_buf[0] & 0x0F) == 0;   // SBEP length nibble 0 is never valid
                if (need > 1100 || badHeader) { ReplayAsFramesLocked(now); return; }
                if (need > 0 && _buf.Count >= need)
                {
                    int op; byte[] data;
                    // Not a valid SBEP message: the radio sent ordinary frames instead (seen on the bench XTL: a second
                    // EPREQ right after the first). Re-read the bytes as SB9600 so no frame is lost.
                    if (!Sbep.Decode(_buf.ToArray(), out op, out data)) { ReplayAsFramesLocked(now); return; }
                    AddItemLocked(new SbItem { Kind = SbItem.Kinds.Sbep, Raw = _buf.ToArray() });
                    SbepMessages++; _buf.Clear(); _inSbep = false; _lastSbepEndMs = now;
                }
                return;
            }
            if (_buf.Count == 0 && b == 0x50 && now - _lastSbepEndMs < 150) return;   // trailing SBEP ACK
            _buf.Add(b);
            int n = _buf.Count;
            if (n >= 5)
            {
                var tail = new byte[5]; _buf.CopyTo(n - 5, tail, 0, 5);
                if (SbCrc.Compute(tail, 0, 4) == tail[4])
                {
                    if (n > 5) { var lead = new byte[n - 5]; _buf.CopyTo(0, lead, 0, n - 5); AddItemLocked(new SbItem { Kind = SbItem.Kinds.Raw, Raw = lead, Text = "Unframed bytes" }); }
                    _buf.Clear(); RxFrames++;
                    var frame = new SbFrame(tail);
                    AddItemLocked(new SbItem { Kind = SbItem.Kinds.Rx, Frame = frame });
                    if (frame.Addr == 0x00 && frame.Func == 0x06 && ((frame.P1 >> 4) & 0x03) == 0x01) { _inSbep = true; _sbepStartMs = now; }
                    return;
                }
                if (n > 64)
                {
                    // Keep the last 4 bytes: they may be the start of a real frame.
                    var junk = new byte[n - 4]; _buf.CopyTo(0, junk, 0, n - 4); _buf.RemoveRange(0, n - 4);
                    AddItemLocked(new SbItem { Kind = SbItem.Kinds.Raw, Raw = junk, Text = "Unframed bytes (no valid CRC)" });
                }
            }
        }

        private void ReplayAsFramesLocked(long now)
        {
            byte[] replay = _buf.ToArray(); _buf.Clear(); _inSbep = false;
            foreach (byte x in replay) AddByteLocked(x, now);
        }
        private void FlushBufferLocked(string why)
        {
            if (_buf.Count == 0) return;
            byte[] raw = _buf.ToArray(); _buf.Clear();
            AddItemLocked(new SbItem { Kind = SbItem.Kinds.Raw, Raw = raw, Text = why });
        }

        // Emits any buffered partial bytes immediately (offline capture decoding).
        public void FlushPending() { lock (_lock) { FlushBufferLocked("Trailing bytes"); _inSbep = false; } }

        private void FlushIdleLocked()
        {
            long now = _clock.ElapsedMilliseconds;
            if (_inSbep)
            {
                if (_buf.Count > 0 && now - _lastByteMs > 200) { FlushBufferLocked("Incomplete SBEP message"); _inSbep = false; }
                else if (_buf.Count == 0 && now - _sbepStartMs > 800) _inSbep = false;
            }
            else if (_buf.Count > 0 && now - _lastByteMs > 80) FlushBufferLocked("Unframed bytes");
        }

        // ---------------- transmit ----------------
        public bool Send(byte addr, byte p1, byte p2, byte func) { return Send(new SbFrame(addr, p1, p2, func)); }
        public bool Send(SbFrame frame)
        {
            if (!IsOpen || !TransmitAllowed || _txQueue == null) return false;
            try { return _txQueue.TryAdd(frame.Bytes()); } catch (Exception) { return false; }
        }
        // Front-panel button: value 0 = release, 1 = press, 2 = toggle (press+release in one message).
        public bool Button(int code, int value) { return code >= 0 && code <= 255 && Send(0x05, (byte)code, (byte)value, 0x57); }

        private void TxLoop()
        {
            BlockingCollection<byte[]> queue = _txQueue;
            try
            {
                foreach (byte[] msg in queue.GetConsumingEnumerable())
                {
                    if (_closing || !ReferenceEquals(queue, _txQueue)) break;
                    try { SendNow(msg); }
                    catch (Exception ex) { Post(SbItem.Kinds.Error, "SB9600 send failed: " + ex.Message); }
                }
            }
            catch (Exception) { }
        }

        private void SendNow(byte[] msg)
        {
            var frame = new SbFrame(msg); frame.Transmitted = true;
            if (_simulated) { PostItem(new SbItem { Kind = SbItem.Kinds.Tx, Frame = frame }); TxFrames++; SimulateRadio(frame); return; }
            SerialPort port = _port; if (port == null || !port.IsOpen) throw new InvalidOperationException("port closed");
            long deadline = _clock.ElapsedMilliseconds + 500;
            while (BusyActive() && _clock.ElapsedMilliseconds < deadline && !_closing) Thread.Sleep(1);
            if (BusyActive()) { TxFailStreak++; _lastTxFailMs = _clock.ElapsedMilliseconds; Post(SbItem.Kinds.Error, "Bus stayed BUSY; " + frame + " not sent (check BUSY wiring/polarity, or the radio is mid-SBEP)."); return; }
            bool expectEcho = _cfg.ExpectEcho;
            SetBusy(true);
            try
            {
                for (int attempt = 1; attempt <= 3 && !_closing; attempt++)
                {
                    lock (_lock) { _echo = msg; _echoPos = 0; _echoLen = expectEcho ? msg.Length : 0; _echoBad = false; _echoDone.Reset(); }
                    port.Write(msg, 0, msg.Length);
                    bool ok = true;
                    if (expectEcho) ok = _echoDone.WaitOne(250) && !_echoBad && _echoPos == _echoLen;
                    else Thread.Sleep(12);
                    lock (_lock) { _echoLen = 0; }
                    if (ok) { TxFrames++; TxFailStreak = 0; PostItem(new SbItem { Kind = SbItem.Kinds.Tx, Frame = frame }); return; }
                    BadEchoes++; Thread.Sleep(3 * attempt);
                }
            }
            finally { SetBusy(false); }
            TxFailStreak++; _lastTxFailMs = _clock.ElapsedMilliseconds;
            Post(SbItem.Kinds.Error, frame + " was not confirmed by its echo after 3 tries (collision, BUSY wiring, or a RIB that does not echo: untick 'Expect echo').");
        }

        private bool BusyActive()
        {
            if (_cfg == null || _port == null || _cfg.BusyIn == BusInLine.None) return false;
            try
            {
                bool level = _cfg.BusyIn == BusInLine.CTS ? _port.CtsHolding : _cfg.BusyIn == BusInLine.DSR ? _port.DsrHolding : _port.CDHolding;
                return _cfg.BusyInInvert ? !level : level;
            }
            catch (Exception) { return false; }
        }
        private void SetBusy(bool on)
        {
            if (_cfg == null || _port == null || _cfg.BusyOut == BusOutLine.None) return;
            bool level = _cfg.BusyOutInvert ? !on : on;
            try { if (_cfg.BusyOut == BusOutLine.DTR) _port.DtrEnable = level; else _port.RtsEnable = level; } catch (Exception) { }
        }
        public string LineStatus()
        {
            if (_simulated) return "Simulated bus (no modem-control lines)";
            if (_port == null || !_port.IsOpen) return "Port closed";
            try { return "CTS=" + (_port.CtsHolding ? "1" : "0") + "  DSR=" + (_port.DsrHolding ? "1" : "0") + "  DCD=" + (_port.CDHolding ? "1" : "0") + "   |   DTR=" + (_port.DtrEnable ? "1" : "0") + "  RTS=" + (_port.RtsEnable ? "1" : "0") + "   |   BUSY " + (BusyActive() ? "ACTIVE" : "idle"); }
            catch (Exception ex) { return ex.Message; }
        }
        public void ToggleLine(string line)
        {
            if (_port == null || !_port.IsOpen) return;
            try { if (line == "DTR") _port.DtrEnable = !_port.DtrEnable; else if (line == "RTS") _port.RtsEnable = !_port.RtsEnable; } catch (Exception) { }
        }

        // ---------------- simulated XTL, for design work ----------------
        private void SimulateRadio(SbFrame f)
        {
            if (f.Addr == 0x05 && f.Func == 0x57)
            {
                bool down = f.P2 == 1 || f.P2 == 2;
                if (f.P1 == 0x01) { SimFrame(0x01, 0x00, f.P2 == 1 ? (byte)1 : (byte)0, 0x19); SimFrame(0x00, 0x03, f.P2 == 1 ? (byte)1 : (byte)0, 0x0A); }
                else if ((f.P1 == 0x51 || f.P1 == 0x50) && down) { SimStepMode(f.P1 == 0x51 ? 1 : -1); SimModeReport(); }
                else if (f.P1 == 0x61 && down) { _simMode = Math.Max(0, Math.Min(SimHome, SimChannels.Count - 1)); SimModeReport(); }
                else if (down && SimZones.Count == SimChannels.Count && SimZones.Count > 0 && (f.P1 == SimZoneUp || f.P1 == SimZoneDown || SimZoneDirect.ContainsKey(f.P1)))
                {
                    int zone = SimZones[Math.Min(_simMode, SimZones.Count - 1)], max = SimZones.Max(), target;
                    if (SimZoneDirect.ContainsKey(f.P1)) target = SimZoneDirect[f.P1];
                    else target = f.P1 == SimZoneUp ? (zone % max) + 1 : ((zone + max - 2) % max) + 1;
                    // Like the bench radio: the new zone keeps the channel position, or channel 1 if the zone is shorter.
                    var members = new List<int>(); for (int i = 0; i < SimZones.Count; i++) if (SimZones[i] == target) members.Add(i);
                    if (members.Count > 0)
                    {
                        int pos = 0, zoneNow = SimZones[Math.Min(_simMode, SimZones.Count - 1)];
                        for (int i = 0; i < _simMode; i++) if (SimZones[i] == zoneNow) pos++;
                        _simMode = members[pos < members.Count ? pos : 0]; SimModeReport();
                    }
                }
            }
            // Direct channel select (W3AXL XTL5000 tools: 01 00 mm 1F then 01 01 mm 60). The simulator numbers modes 0..n-1 in
            // codeplug order; a real radio's numbering is learned by the console from the display.
            else if (f.Addr == 0x01 && f.Func == 0x1F && f.P1 == 0x00)
            {
                if (f.P2 >= 1 && f.P2 <= SimChannels.Count) { _simMode = f.P2 - 1; SimModeReport(); }
            }
        }
        private void SimStepMode(int delta)
        {
            int count = Math.Max(1, SimChannels.Count);
            if (SimZones.Count != SimChannels.Count || SimZones.Count == 0) { _simMode = (_simMode + delta + count) % count; return; }
            int zone = SimZones[Math.Min(_simMode, SimZones.Count - 1)];
            var members = new List<int>(); for (int i = 0; i < SimZones.Count; i++) if (SimZones[i] == zone) members.Add(i);
            int at = members.IndexOf(_simMode); _simMode = members[((at < 0 ? 0 : at) + delta + members.Count) % members.Count];
        }
        private void SimModeReport()
        {
            // The real XTL sends the display first and its mode report about 600 ms later, numbered from 1 in codeplug
            // order (bench log: FD1 RPT = 1, STATE FD = 2, MURS 1 = 53).
            string name = _simMode < SimChannels.Count ? SimChannels[_simMode] : "MODE " + (_simMode + 1);
            name = (name.Length > 14 ? name.Substring(0, 14) : name).PadRight(14);
            SimSbep(Sbep.DisplayMessage(0, 0, name));
            _simModeDue = _clock.ElapsedMilliseconds + SimModeLagMs; _simModePending = Math.Min(255, _simMode + 1);
        }
        private void SimFrame(byte a, byte b, byte c, byte func) { Feed(new SbFrame(a, b, c, func).Bytes(), 5); }
        private void SimSbep(byte[] sbep)
        {
            SimFrame(0x00, 0x12, 0x05, 0x06);       // EPREQ: SBEP at 9600 to the panel
            var withAck = new byte[sbep.Length + 1]; withAck[0] = 0x50; Array.Copy(sbep, 0, withAck, 1, sbep.Length);
            Feed(withAck, withAck.Length);
        }
        public void InjectSimulated(byte a, byte b, byte c, byte func) { if (_simulated) SimFrame(a, b, c, func); }
        public void InjectSimulatedIndicator(byte code, byte state) { if (_simulated) SimSbep(Sbep.Encode(0x21, new byte[] { 1, code, state })); }
    }
}
