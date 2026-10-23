using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Reads an ASTRO 25 Mobile CPS "Export to Applications" CSV (Zone Channel Assignment + Scan List features)
// and turns it into console channels: zone/position, 8-character radio text, full name, frequencies, squelch,
// personality, receive-only and scan membership.
namespace RadioDispatchConsole
{
    internal sealed class CpsChannel
    {
        public int Zone, Position, Personality;
        public string ZoneName = "", Label = "", RxType = "", RxPl = "", RxDpl = "", TxType = "", TxPl = "", TxDpl = "";
        public bool RxDplInvert, TxDplInvert;
        public double RxMHz, TxMHz;
    }

    internal sealed class CpsImportResult
    {
        public readonly List<CpsChannel> Channels = new List<CpsChannel>();
        public readonly List<KeyValuePair<int, int>> Scan = new List<KeyValuePair<int, int>>();   // zone, channel
        public int Priority1 = -1, Priority2 = -1;   // index into Scan
        public readonly List<string> Warnings = new List<string>();
    }

    internal static class CpsImport
    {
        public static List<string> SplitCsv(string line)
        {
            var result = new List<string>(); var sb = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            result.Add(sb.ToString());
            return result;
        }

        private static double Num(string s) { double d; return Double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0; }
        private static int Int(string s) { int n; return Int32.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0; }
        private static int ZoneNumberFrom(string zoneName, int fallback)
        {
            string digits = new string((zoneName ?? "").Where(ch => Char.IsDigit(ch)).ToArray());
            int n = Int(digits); return n > 0 ? n : fallback;
        }

        public static CpsImportResult Parse(string[] lines)
        {
            var r = new CpsImportResult();
            string feature = "", section = "", zoneName = ""; int zoneIndex = 0, position = 0;
            List<string> fields = null; bool values = false;
            var scanGeneral = new Dictionary<string, string>();
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].TrimEnd('\r');
                if (line.Trim().Length == 0) { values = false; continue; }
                List<string> cells = SplitCsv(line);
                string head = cells[0].Trim();
                if (head == "__Feature__") { feature = cells.Count > 1 ? cells[1] : ""; section = ""; values = false; continue; }
                if (head == "__Record__") { values = false; if (feature == "Zone Channel Assignment") { zoneIndex++; position = 0; zoneName = "Z" + zoneIndex; } continue; }
                if (head == "__Section__") { section = cells.Count > 1 ? cells[1] : ""; values = false; continue; }
                if (head == "__Field Name__") continue;
                if (head == "__Field Names__") { if (n + 1 < lines.Length) { fields = SplitCsv(lines[n + 1].TrimEnd('\r')); n++; } values = false; continue; }
                if (head == "__Field Values__") { values = true; continue; }
                if (head.StartsWith("__")) continue;
                if (!values)
                {
                    // name/value pair lines inside a section
                    if (cells.Count >= 2)
                    {
                        if (feature == "Zone Channel Assignment" && section == "Zone" && cells[0] == "Zone Name") zoneName = cells[1];
                        if (feature == "Scan List" && section == "General") scanGeneral[cells[0]] = cells[1];
                    }
                    continue;
                }
                if (fields == null) continue;
                Func<string, string> get = delegate(string name) { int i = fields.IndexOf(name); return i >= 0 && i < cells.Count ? cells[i] : ""; };
                if (feature == "Zone Channel Assignment" && section == "Channels")
                {
                    position++;
                    var ch = new CpsChannel
                    {
                        Zone = ZoneNumberFrom(zoneName, zoneIndex), ZoneName = zoneName, Position = position, Label = get("Channel Name"),
                        Personality = Int(get("Personality #")), RxMHz = Num(get("Receive Frequency")), TxMHz = Num(get("Transmit Frequency")),
                        RxType = get("Receive Squelch Type"), RxPl = get("Receive PL Freq"), RxDpl = get("Receive DPL Code"), RxDplInvert = get("Receive DPL Invert") == "True",
                        TxType = get("Transmit Squelch Type"), TxPl = get("Transmit PL Freq"), TxDpl = get("Transmit DPL Code"), TxDplInvert = get("Transmit DPL Invert") == "True"
                    };
                    if (ch.Label.Length == 0) r.Warnings.Add("Zone " + zoneName + " row " + position + " has no channel name.");
                    r.Channels.Add(ch);
                }
                else if (feature == "Scan List" && section == "List")
                {
                    int z = Int(get("Zone")), c = Int(get("Channel"));
                    if (z > 0 && c > 0) r.Scan.Add(new KeyValuePair<int, int>(z, c));
                }
            }
            string p1, p2;
            if (scanGeneral.TryGetValue("Priority 1 Member", out p1) && Int(p1) > 0) r.Priority1 = Int(p1) - 1;
            if (scanGeneral.TryGetValue("Priority 2 Member", out p2) && Int(p2) > 0) r.Priority2 = Int(p2) - 1;
            if (r.Channels.Count == 0) r.Warnings.Add("No 'Zone Channel Assignment' channels were found. Export the Zone Channel Assignment feature from CPS.");
            return r;
        }

        public static string Squelch(string type, string pl, string dpl, bool invert)
        {
            if (type == "PL") return "PL " + pl;
            if (type == "DPL") return "DPL " + dpl + (invert ? "I" : "");
            if (type == "CSQ") return "CSQ";
            if (type == "Disabled") return "CSQ";
            return type ?? "";
        }

        // Built-in full names for common national labels. Your own names go in cps\channel_names.csv
        // (RadioText,FullName); they are read on top of this list and take priority.
        public static Dictionary<string, string> DefaultNames()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] pairs = {
                "VCALL 10","VCALL 10", "VTAC 11","VTAC 11", "VTAC 12","VTAC 12", "VTAC 13","VTAC 13", "VTAC 14","VTAC 14",
                "MURS 1","MURS 1", "MURS 2","MURS 2", "MURS 3","MURS 3", "MURS 4","MURS 4", "MURS 5","MURS 5"
            };
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
            return d;
        }

        // First run: when cps\Example_XTL5000_Channels.csv ships beside the console, load it into the XTL radio with the
        // example defaults (RX-only personalities 3 and 6, Home Z1/2, No Mode Select, SB9600 channel select). Replace the
        // file with your own CPS export (same name) to start from your codeplug. Returns "" if not applied.
        public static string ApplyShipped(RadioProfile radio, string directory)
        {
            string folder = Path.Combine(directory ?? "", "cps"), file = Path.Combine(folder, "Example_XTL5000_Channels.csv");
            if (radio == null || !File.Exists(file)) return "";
            CpsImportResult result = Parse(File.ReadAllLines(file));
            if (result.Channels.Count == 0 || result.Channels.Count > 512) return "";
            List<ChannelProfile> channels = ToChannels(result, LoadNames(folder), new HashSet<int> { 3, 6 }, true);
            int home = channels.FindIndex(c => c.ZoneNumber == 1 && c.ZonePosition == 2); if (home < 0) home = 0;
            radio.Channels.Clear(); radio.Channels.AddRange(channels);
            radio.HomeChannel = home; radio.SelectedChannel = home;
            radio.ChannelSelect = ChannelMethod.Sb9600ModeKeys; radio.Bus.SyncChannelFromDisplay = true;
            radio.Bus.W7BenchKeymap = true; ApplyW7BenchKeymap(radio);
            return "Loaded the example codeplug (" + channels.Count + " channels, " + channels.Select(c => c.ZoneNumber).Distinct().Count() + " zones) from cps\\Example_XTL5000_Channels.csv. Home = " + channels[home].Name + ".";
        }

        // What the development consolette showed on the bench (SB9600 log, 20 Oct 2026): keypad 1 = zone up and keypad 3 = zone
        // down (codeplug-programmed; the channel position is kept), and mode reports count from 1 in codeplug order.
        // Applied only when the radio's Bus.W7BenchKeymap is on (set by the first-run example codeplug load); LEARN overrides
        // any of it. Turn W7BenchKeymap off if your codeplug programs the keypad differently.
        public static void ApplyW7BenchKeymap(RadioProfile radio)
        {
            if (radio == null || radio.Bus == null || !radio.Bus.W7BenchKeymap) return;
            radio.Bus.EnsureDefaults();   // the standard W-series keys first; the W7 bench extras go after them
            List<HeadButton> hb = radio.Bus.HeadButtons;
            // Bench W7 (bench log 3, 20 Oct 2026): the Sel top key is 0x67 (top key 5, pressed right after Call 0x66) and DIM is
            // 0x60 (pressed four times between the volume keys and HOME). Only change entries still holding the W9 defaults.
            HeadButton sel = hb.Find(b => b.Name == "SEL"), dim = hb.Find(b => b.Name == "DIM");
            if (sel != null && sel.Code == 0x60) { sel.Code = 0x67; sel.Note = "Bench W7: Sel = top key 5 (bench log 3)"; }
            if (dim != null && dim.Code == 0x62) { dim.Code = 0x60; dim.Note = "Bench W7: DIM (bench log 3)"; }
            if (!hb.Exists(b => b.Name == "ZONE UP")) hb.Add(new HeadButton("ZONE UP", 0x31, "bench codeplug: keypad 1 (bench log 20 Oct 2026)"));
            if (!hb.Exists(b => b.Name == "ZONE DN")) hb.Add(new HeadButton("ZONE DN", 0x33, "bench codeplug: keypad 3 (bench log 20 Oct 2026)"));
            if (!hb.Exists(b => b.Name == "MON")) hb.Add(new HeadButton("MON", 0x38, "bench codeplug: keypad 8 = monitor (bench log 20 Oct 2026)"));
            if (!hb.Exists(b => b.Name == "SCAN")) hb.Add(new HeadButton("SCAN", 0x65, "Bench W7: top key 3 toggles scan (bench log 20 Oct 2026)"));
            if (!hb.Exists(b => b.Name == "NUIS DEL")) hb.Add(new HeadButton("NUIS DEL", 0x3B, "bench codeplug: keypad # = scan nuisance delete"));
            radio.Bus.ChannelBase = 1;
            // Bench operator, 20 Oct 2026: Emer and Phon are not used; keypad keys other than 1, 3, 8 and # do nothing.
            // The DIR light (indicator 0x06) follows the channel: lit on simplex channels (bench logs).
            if (String.IsNullOrWhiteSpace(radio.Bus.UnusedKeys)) radio.Bus.UnusedKeys = "TOP1 TOP2 2 4 5 6 7 9 0 *";
            if (radio.Bus.IndicatorDirect < 0) radio.Bus.IndicatorDirect = 0x06;
        }

        public static Dictionary<string, string> LoadNames(string folder)
        {
            Dictionary<string, string> names = DefaultNames();
            string file = Path.Combine(folder ?? "", "channel_names.csv");
            if (!File.Exists(file)) return names;
            foreach (string line in File.ReadAllLines(file))
            {
                List<string> c = SplitCsv(line);
                if (c.Count >= 2 && c[0].Trim().Length > 0 && !c[0].Trim().Equals("RadioText", StringComparison.OrdinalIgnoreCase) && c[1].Trim().Length > 0) names[c[0].Trim()] = c[1].Trim();
            }
            return names;
        }

        // Builds console channels. noModeSelect: every channel uses 1950 Hz purely as the PTT function tone (TRC tone table
        // "No Mode Sel", S100-6 ON) and channels are changed over SB9600.
        public static List<ChannelProfile> ToChannels(CpsImportResult r, Dictionary<string, string> names, HashSet<int> rxOnlyPersonalities, bool noModeSelect)
        {
            var list = new List<ChannelProfile>();
            int[] standard = { 1950, 1850, 1350, 1250, 1550, 1450, 1750, 1650 };
            foreach (CpsChannel c in r.Channels)
            {
                string full; if (names == null || !names.TryGetValue(c.Label.Trim(), out full)) full = c.Label.Trim();
                var ch = new ChannelProfile
                {
                    Zone = "ZONE " + c.Zone, Name = full.Length > 0 ? full : "CH " + c.Position, RadioText = c.Label.Trim(), ZoneNumber = c.Zone, ZonePosition = c.Position,
                    RxMHz = c.RxMHz, TxMHz = c.TxMHz, Personality = c.Personality, RxOnly = rxOnlyPersonalities != null && rxOnlyPersonalities.Contains(c.Personality),
                    RxTone = Squelch(c.RxType, c.RxPl, c.RxDpl, c.RxDplInvert), TxTone = Squelch(c.TxType, c.TxPl, c.TxDpl, c.TxDplInvert),
                    CtcssHz = c.RxType == "PL" ? Num(c.RxPl) : 0
                };
                ch.FunctionHz = noModeSelect ? 1950 : (c.Zone == 1 && c.Position <= 8 ? standard[c.Position - 1] : 1950);
                list.Add(ch);
            }
            for (int i = 0; i < r.Scan.Count; i++)
            {
                KeyValuePair<int, int> m = r.Scan[i];
                ChannelProfile ch = list.FirstOrDefault(x => x.ZoneNumber == m.Key && x.ZonePosition == m.Value);
                if (ch == null) { r.Warnings.Add("Scan member Z" + m.Key + "/" + m.Value + " does not match a channel."); continue; }
                ch.ScanMember = true;
                if (i == r.Priority1) ch.ScanPriority = 1; else if (i == r.Priority2) ch.ScanPriority = 2;
            }
            return list;
        }
    }
}
