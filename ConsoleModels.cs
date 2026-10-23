using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

// Dispatch Console - configuration model.
// C# 5 compatible on purpose (the Windows .NET Framework csc.exe is C# 5).
namespace RadioDispatchConsole
{
    public enum PttMethod { TrcTones, Sb9600Button }
    // Which jack of a multichannel (5.1/7.1) onboard output carries the radio TX audio. WholeDevice = dedicated sound card.
    public enum TxJack { WholeDevice, Side71, Rear71, CenterBass71 }
    public enum ChannelMethod { TrcTone, Sb9600ModeKeys, Manual }
    public enum BusOutLine { None, DTR, RTS }
    public enum BusInLine { None, CTS, DSR, DCD }
    public enum RuleEvent { None, RxStart, RxEnd, TxStart, TxEnd, Emergency, EmergencyClear, MonitorOn, MonitorOff, LogOnly }
    public enum CardDensity { Compact, Normal, Large }
    public enum CardPlacement { Left, Top }
    public enum LayoutMode { Auto, Full, Compact }
    public enum StartupMode { Live, ReceiveOnly, Test }
    public enum FootswitchSource { Off, KeyboardKey, GameController, ComPortLine }
    public enum SerialLine { CTS, DSR, DCD }

    // Footswitch PTT (see Footswitch.cs): keyboard-style USB pedal, game-controller pedal or a switch on a COM port line.
    public class FootswitchSettings
    {
        [Description("Off; KeyboardKey = a USB pedal that types a key (most USB pedals); GameController = a USB pedal Windows lists under 'Set up USB game controllers'; ComPortLine = a switch wired to a COM port. All three work even when another program (CAD, softphone) has focus.")]
        public FootswitchSource Source { get; set; }
        [Description("KeyboardKey pedals: the key the pedal types. Program the pedal to a key nothing else uses (F13-F24, Scroll, Pause); the console catches it system-wide and keeps it from other programs.")]
        public string Key { get; set; }
        [Description("Game controller number as Windows numbers them (1 = the first one plugged in).")]
        public int ControllerNumber { get; set; }
        [Description("Controller button that keys PTT (1-32). Press the pedal and watch the FOOT light to find it.")]
        public int Button { get; set; }
        [TypeConverter(typeof(ComPortConverter)), Description("COM port for a wired switch (not the RIB port). The console holds DTR and RTS high to supply the switch.")]
        public string PortName { get; set; }
        [Description("Input line the switch closes: CTS (DB9 pin 8, wire to RTS pin 7), DSR (pin 6, wire to DTR pin 4) or DCD (pin 1, wire to DTR pin 4).")]
        public SerialLine Line { get; set; }
        [Description("Tick for a normally-closed pedal (PTT while the contact is open).")]
        public bool Invert { get; set; }
        public FootswitchSettings() { Source = FootswitchSource.Off; Key = "F13"; ControllerNumber = 1; Button = 1; PortName = ""; Line = SerialLine.CTS; Invert = false; }
    }

    public class ChannelProfile
    {
        public string Zone { get; set; }
        public string Name { get; set; }
        [Description("Tone-remote function tone (Hz) that selects/keys this mode on the consolette tone table.")]
        public int FunctionHz { get; set; }
        public double CtcssHz { get; set; }
        [Description("What the radio's own (8-14 character) display shows on this channel, e.g. CITY FD. When the SB9600 display shows this text, the console selects this channel and shows its full Name.")]
        public string RadioText { get; set; }
        [Category("Codeplug"), Description("Zone number in the radio (1-based). 0 = not imported from CPS.")]
        public int ZoneNumber { get; set; }
        [Category("Codeplug"), Description("Channel position within its zone in the radio (1-based).")]
        public int ZonePosition { get; set; }
        [Category("Codeplug"), Description("Receive frequency in MHz (information only; the radio's codeplug is authoritative).")]
        public double RxMHz { get; set; }
        [Category("Codeplug")] public double TxMHz { get; set; }
        [Category("Codeplug"), Description("Receive squelch, e.g. PL 186.2, DPL 032, CSQ.")]
        public string RxTone { get; set; }
        [Category("Codeplug")] public string TxTone { get; set; }
        [Category("Codeplug")] public int Personality { get; set; }
        [Category("Codeplug"), Description("Receive-only in the codeplug. The console will not key PTT, alerts or pages on this channel.")]
        public bool RxOnly { get; set; }
        [Category("Codeplug"), Description("Member of the radio's scan list.")]
        public bool ScanMember { get; set; }
        [Category("Codeplug"), Description("Scan priority (1 or 2), 0 = none.")]
        public int ScanPriority { get; set; }
        // v0.6.1 settings files used these names. They are read once (and converted on load), never written.
        [Browsable(false)] public bool FromCodeplug { get { return LegacyV061; } set { if (value) LegacyV061 = true; } }
        [Browsable(false)] public int ChannelNumber { get { return 0; } set { if (value > 0 && ZonePosition == 0) ZonePosition = value; } }
        [Browsable(false)] public bool ReceiveOnly { get { return RxOnly; } set { if (value) RxOnly = true; } }
        public bool ShouldSerializeFromCodeplug() { return false; }
        public bool ShouldSerializeChannelNumber() { return false; }
        public bool ShouldSerializeReceiveOnly() { return false; }
        [XmlIgnore, Browsable(false)] public bool LegacyV061;
        [Category("Codeplug"), Description("SB9600 mode number for direct channel select, learned from the radio the first time a direct select is confirmed by its display. -1 = not learned yet.")]
        public int BusMode { get; set; }
        public ChannelProfile() { BusMode = -1; Zone = "OPERATIONS"; Name = "New channel"; FunctionHz = 1950; RadioText = ""; RxTone = ""; TxTone = ""; }
        public override string ToString() { return Zone + " / " + (ZonePosition > 0 ? ZonePosition + " " : "") + Name + (RxOnly ? "  (RX ONLY)" : ""); }
        // No tone ("None" / "Disabled" in older imports) reads as CSQ (carrier squelch), the way the radio shows it.
        private static string ToneText(string tone)
        {
            string t = (tone ?? "").Trim();
            return t.Length == 0 || t.Equals("None", StringComparison.OrdinalIgnoreCase) || t.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? "CSQ" : t;
        }
        [Browsable(false)] public string FreqText
        {
            get
            {
                if (RxMHz <= 0) return "";
                string rx = "RX " + RxMHz.ToString("0.0000", CultureInfo.InvariantCulture) + " " + ToneText(RxTone);
                string tx = RxOnly ? "TX disabled" : TxMHz > 0 ? "TX " + TxMHz.ToString("0.0000", CultureInfo.InvariantCulture) + " " + ToneText(TxTone) : "";
                return rx.Trim() + "  |  " + tx.Trim();
            }
        }
    }

    public class SoftKeyProfile
    {
        public string Label { get; set; }
        [Description("Action string, e.g. head:HOME, func:SCAN, trc:A, chan:3, alert:BEEPER, sb:05 83 01 57. See README.")]
        public string Binding { get; set; }
        public SoftKeyProfile() { Label = "CUSTOM"; Binding = "unassigned"; }
        public SoftKeyProfile(string label, string binding) { Label = label; Binding = binding; }
        public override string ToString() { return Label + " [" + Binding + "]"; }
    }

    public class HeadButton
    {
        public string Name { get; set; }
        [Browsable(false)] public int Code { get; set; }
        [XmlIgnore, DisplayName("Code (hex)"), Description("Front-panel control code, e.g. 0x61.")]
        public string CodeHex
        {
            get { return "0x" + Code.ToString("X2"); }
            set { string v = (value ?? "").Trim(); if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v.Substring(2); int n; if (Int32.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 255) Code = n; else throw new FormatException("Use a hex byte such as 0x61."); }
        }
        public string Note { get; set; }
        public HeadButton() { Name = "BTN"; Code = 0; Note = ""; }
        public HeadButton(string name, int code, string note) { Name = name; Code = code; Note = note; }
        public override string ToString() { return Name + " (0x" + Code.ToString("X2") + ")"; }
    }

    // Expands short radio-display text into a full name, e.g. "CITY FD" -> "CITY FIRE DEPT".
    public class TextLookup
    {
        [Description("Text as the radio displays it (not case sensitive).")]
        public string Match { get; set; }
        [Description("Full text the console shows instead.")]
        public string Replace { get; set; }
        [Description("Off = whole display must equal Match. On = also replace Match where it appears as a word inside the display.")]
        public bool PartialWord { get; set; }
        public TextLookup() { Match = ""; Replace = ""; }
        public TextLookup(string match, string replace) { Match = match; Replace = replace; }
        public override string ToString() { return Match + " -> " + Replace; }
    }

    internal static class DisplayText
    {
        private static string Clean(string s) { return System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim(), @"\s+", " "); }
        // Channel whose RadioText (preferred) or Name equals the radio display; -1 if none, or if the text matches more
        // than one channel (e.g. FIRE MA is in zones 1 and 3) - a repeated label alone cannot say which zone the radio is in.
        // The closed-loop channel seek resolves those using the zone it last confirmed (ConsoleWindow.LocateDisplayed).
        public static int FindChannel(RadioProfile radio, string display)
        {
            List<int> hits = Matches(radio, display);
            return hits.Count == 1 ? hits[0] : -1;
        }
        public static List<int> Matches(RadioProfile radio, string display)
        {
            var hits = new List<int>(); string text = Clean(display); if (text.Length == 0) return hits;
            for (int i = 0; i < radio.Channels.Count; i++) { string rt = Clean(radio.Channels[i].RadioText); if (rt.Length > 0 && String.Equals(rt, text, StringComparison.OrdinalIgnoreCase)) hits.Add(i); }
            if (hits.Count > 0) return hits;
            for (int i = 0; i < radio.Channels.Count; i++) if (String.Equals(Clean(radio.Channels[i].Name), text, StringComparison.OrdinalIgnoreCase)) hits.Add(i);
            return hits;
        }
        // Full text for a radio display line: channel match, then whole-text lookups, then partial-word lookups.
        public static string Expand(RadioProfile radio, string display)
        {
            string text = Clean(display); if (text.Length == 0) return "";
            List<int> hits = Matches(radio, text);
            if (hits.Count > 0 && hits.TrueForAll(i => radio.Channels[i].Name == radio.Channels[hits[0]].Name)) return radio.Channels[hits[0]].Name;
            List<TextLookup> lookups = radio.Bus != null ? radio.Bus.DisplayLookups : null;
            if (lookups == null) return text;
            foreach (TextLookup l in lookups) if (l != null && Clean(l.Match).Length > 0 && String.Equals(Clean(l.Match), text, StringComparison.OrdinalIgnoreCase)) return l.Replace ?? text;
            string result = text;
            foreach (TextLookup l in lookups)
                if (l != null && l.PartialWord && Clean(l.Match).Length > 0)
                    result = System.Text.RegularExpressions.Regex.Replace(result, @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(Clean(l.Match)) + @"(?![A-Za-z0-9])", (l.Replace ?? "").Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return result;
        }
    }

    public class BusRule
    {
        public string Name { get; set; }
        [Description("Four hex bytes: address param1 param2 function. Use ** as a wildcard, e.g. 01 ** 03 1E")]
        public string Pattern { get; set; }
        public RuleEvent Event { get; set; }
        public bool Enabled { get; set; }
        public BusRule() { Name = "Rule"; Pattern = "** ** ** **"; Event = RuleEvent.LogOnly; Enabled = true; }
        public BusRule(string name, string pattern, RuleEvent ev) { Name = name; Pattern = pattern; Event = ev; Enabled = true; }
        public override string ToString() { return Name + " [" + Pattern + "] -> " + Event; }
    }

    public class BusSettings
    {
        [Category("Serial"), Description("COM port of the RIB (RS-232, 9600 baud 8N1).")]
        [TypeConverter(typeof(ComPortConverter))]
        public string PortName { get; set; }
        [Category("Serial")] public int Baud { get; set; }
        [Category("BUSY handshake"), Description("Modem-control output that asserts BUSY on the RIB.")]
        public BusOutLine BusyOut { get; set; }
        public bool BusyOutInvert { get; set; }
        [Category("BUSY handshake"), Description("Modem-control input that reports BUSY from the RIB.")]
        public BusInLine BusyIn { get; set; }
        public bool BusyInInvert { get; set; }
        [Category("BUSY handshake"), Description("The RIB echoes every transmitted byte. When on, a send is confirmed by its echo.")]
        public bool ExpectEcho { get; set; }
        [Category("Behaviour")] public bool AutoConnect { get; set; }
        [Category("Behaviour"), Description("Bus channel numbers are offset by this much from the channel list index (0 or 1).")]
        public int ChannelBase { get; set; }
        [Category("Behaviour"), Description("Allow this console to send frames on the bus. Off = passive listen only.")]
        public bool AllowTransmit { get; set; }
        [Category("Behaviour"), Description("Send the SB9600 reset (00 00 01 08) after connecting. The radio restarts and re-sends its display text. RadioConsole2 does this by default.")]
        public bool ResetOnConnect { get; set; }
        [Category("Behaviour"), Description("Treat the head's PRI / NON-PRI indicators as receive activity (useful while scanning).")]
        public bool UseLedsForRx { get; set; }
        [Category("BUSY handshake"), Description("On connect, assert BUSY and confirm the RIB reports it back (RadioConsole2 self-test).")]
        public bool TestBusyOnConnect { get; set; }
        [Category("Behaviour"), Description("Follow a channel change made at the radio itself (the radio's mode report confirms it). Traffic on another channel while scanning only shows in the display at the top; the zone / channel keys stay on the operator's channel.")]
        public bool SyncChannelFromDisplay { get; set; }
        [Category("Behaviour"), Description("When SB9600 opens (and when you click a light showing \"?\"), press VOL UP then VOL DN (the radio redraws its display; the volume ends where it was) and SCAN twice (the radio reports its scan light; scan ends where it started). The console then takes the channel the radio shows. Needs AllowTransmit.")]
        public bool SyncOnConnect { get; set; }
        [Category("Behaviour"), Description("Change channel with one direct 'go to mode' command (01 00 mm 1F + 01 01 mm 60, as in W3AXL's XTL5000 tools) and confirm it from the radio display. If the radio ignores it, the console falls back to pressing ZONE / MODE keys. Turn off to always use the keys.")]
        public bool DirectChannelGoto { get; set; }
        [Category("Indicators"), Description("SBEP indicator code of the radio's SCAN light. 0x02 on the bench W7 (bench log). -1 = not mapped.")]
        public int IndicatorScan { get; set; }
        [Category("Indicators"), Description("SBEP indicator code for monitor, if your radio has one (the bench radio reports monitor as DISPLY field 0x05 instead). -1 = not mapped.")]
        public int IndicatorMonitor { get; set; }
        [Category("Indicators"), Description("SBEP indicator code of the DIRECT / talkaround light. Toggle DIR on the radio and read the code in Settings > Engineering > SB9600 monitor ('SBEP indicators: 0x.. = on'). -1 = not mapped.")]
        public int IndicatorDirect { get; set; }
        [Category("Indicators"), Description("SBEP indicator code of the LOW POWER light (see IndicatorDirect for how to find it). -1 = not mapped.")]
        public int IndicatorLowPower { get; set; }
        [Category("Indicators"), Description("SBEP indicator code of the SECURE light (see IndicatorDirect for how to find it). -1 = not mapped.")]
        public int IndicatorSecure { get; set; }
        [Category("W7 faceplate"), Description("W7 keys that do nothing on this codeplug, shown greyed out on the console's W7 panel. Space-separated head-control names, e.g. TOP1 TOP2 2 4 5 6 7 9 0 *.")]
        public string UnusedKeys { get; set; }
        [Category("W7 faceplate"), Description("Add the development bench's W7 key map on load: keypad 1 / 3 = ZONE UP / DN, 8 = monitor, # = nuisance delete, top key 3 = SCAN, Sel 0x67, DIM 0x60, DIR light 0x06. Turned on by the first-run example codeplug. Turn off if your codeplug programs the keypad differently, then LEARN your own keys.")]
        public bool W7BenchKeymap { get; set; }
        [Category("Behaviour"), Description("Commands (monitor, secure, scan, channel) go over SB9600 first. If the bus is not connected, is listen-only, or its last two frames failed, the console falls back to the TRC tone for that command (monitor 2050 Hz, secure 1150/1050 Hz). PTT and voice always use TRC.")]
        public bool PreferBusCommands { get; set; }
        [Category("Behaviour"), Description("Learned: radio mode number minus console channel index. Set automatically after the first confirmed direct select.")]
        public int GotoOffset { get; set; }
        [Category("Behaviour"), Description("True once GotoOffset has been confirmed by the radio.")]
        public bool GotoOffsetKnown { get; set; }
        [Category("Serial"), Description("Control head type label. Default codes are the W-series (Astro W9) map used by RadioConsole2 for XTL5000 consolettes.")]
        public string HeadType { get; set; }
        [Category("W7 faceplate"), Description("Top key 1 legend.")] public string W7Top1Label { get; set; }
        [Category("W7 faceplate"), Description("Top key 1 head-control name from the Head buttons list; verify with LEARN.")] public string W7Top1Binding { get; set; }
        [Category("W7 faceplate"), Description("Top key 2 legend.")] public string W7Top2Label { get; set; }
        [Category("W7 faceplate"), Description("Top key 2 head-control name from the Head buttons list; verify with LEARN.")] public string W7Top2Binding { get; set; }
        [Category("W7 faceplate"), Description("Top key 3 legend.")] public string W7Top3Label { get; set; }
        [Category("W7 faceplate"), Description("Top key 3 head-control name from the Head buttons list; verify with LEARN.")] public string W7Top3Binding { get; set; }
        [Category("W7 faceplate"), Description("Top key 4 legend.")] public string W7Top4Label { get; set; }
        [Category("W7 faceplate"), Description("Top key 4 head-control name from the Head buttons list; verify with LEARN.")] public string W7Top4Binding { get; set; }
        [Browsable(false)] public List<HeadButton> HeadButtons { get; set; }
        [Browsable(false)] public List<BusRule> Rules { get; set; }
        [Browsable(false)] public List<TextLookup> DisplayLookups { get; set; }
        public BusSettings()
        {
            PortName = ""; Baud = 9600; BusyOut = BusOutLine.DTR; BusyOutInvert = false; BusyIn = BusInLine.CTS; BusyInInvert = false;
            ExpectEcho = true; AutoConnect = false; ChannelBase = 1; AllowTransmit = false; ResetOnConnect = false; UseLedsForRx = false; TestBusyOnConnect = true; SyncChannelFromDisplay = true; SyncOnConnect = true; DirectChannelGoto = true; PreferBusCommands = true;
            UnusedKeys = ""; IndicatorScan = 0x02; IndicatorMonitor = -1; IndicatorDirect = -1; IndicatorLowPower = -1; IndicatorSecure = -1; HeadType = "W7 (W-series codes)";
            HeadButtons = new List<HeadButton>(); Rules = new List<BusRule>(); DisplayLookups = new List<TextLookup>();
        }
        public void EnsureDefaults()
        {
            if (Baud < 1200 || Baud > 115200) Baud = 9600;
            if (HeadButtons == null) HeadButtons = new List<HeadButton>();
            if (Rules == null) Rules = new List<BusRule>();
            if (DisplayLookups == null) DisplayLookups = new List<TextLookup>();
            if (HeadType == null) HeadType = "W7 (W-series codes)";
            if (W7Top1Label == null) W7Top1Label = ""; if (W7Top2Label == null) W7Top2Label = ""; if (W7Top3Label == null) W7Top3Label = ""; if (W7Top4Label == null) W7Top4Label = "";
            if (UnusedKeys == null) UnusedKeys = "";
            if (W7Top1Binding == null) W7Top1Binding = ""; if (W7Top2Binding == null) W7Top2Binding = ""; if (W7Top3Binding == null) W7Top3Binding = ""; if (W7Top4Binding == null) W7Top4Binding = "";
            // Every standard W-series key must exist by name. (v0.7 first-run bug: the W7 bench extras were added before the
            // defaults, so "list not empty" skipped them and MODE UP/DN, HOME, SEL ... were missing.) Merge any missing ones.
            var defaults = new List<HeadButton>(); AddWSeriesButtons(defaults);
            foreach (HeadButton d in defaults) if (!HeadButtons.Exists(b => b != null && String.Equals(b.Name, d.Name, StringComparison.OrdinalIgnoreCase))) HeadButtons.Add(d);
            if (Rules.Count == 0)
            {
                // Built-in decoding already handles TX (RADKEY/SETBUT), RX (SQLDET), monitor and mode.
                // Rules add extra events. This example is disabled until you LEARN the real frame.
                var example = new BusRule("Example: emergency (learn the real frame)", "** ** ** **", RuleEvent.Emergency); example.Enabled = false;
                Rules.Add(example);
            }
        }
        // Astro W-series (W9) control codes from RadioConsole2's XTL5000 implementation.
        public static void AddWSeriesButtons(List<HeadButton> list)
        {
            string note = "W-series map (RadioConsole2); verify on W7 with LEARN";
            list.Add(new HeadButton("PTT", 0x01, note)); list.Add(new HeadButton("VIP1", 0x06, note)); list.Add(new HeadButton("VIP2", 0x07, note));
            list.Add(new HeadButton("RADIO SEL", 0x10, note)); list.Add(new HeadButton("RSSI", 0x11, note)); list.Add(new HeadButton("SPKR", 0x12, note));
            for (int d = 0; d <= 9; d++) list.Add(new HeadButton(d.ToString(), 0x30 + d, note));
            list.Add(new HeadButton("*", 0x3A, note)); list.Add(new HeadButton("#", 0x3B, note));
            list.Add(new HeadButton("MODE DN", 0x50, note)); list.Add(new HeadButton("MODE UP", 0x51, note));
            list.Add(new HeadButton("VOL DN", 0x52, note)); list.Add(new HeadButton("VOL UP", 0x53, note));
            list.Add(new HeadButton("SEL", 0x60, note)); list.Add(new HeadButton("HOME", 0x61, note)); list.Add(new HeadButton("DIM", 0x62, note));
            for (int k = 1; k <= 6; k++) list.Add(new HeadButton("TOP" + k, 0x62 + k, note));
        }
        public override string ToString() { return String.IsNullOrEmpty(PortName) ? "(no port)" : PortName; }
    }

    public class ToneProfile
    {
        [Category("Guard tone")] public double GuardHz { get; set; }
        [Category("Guard tone"), Description("High-level guard tone duration. Motorola TRC default 120 ms.")]
        public int GuardMs { get; set; }
        [Category("Function tone"), Description("Function tone duration. Motorola TRC default 40 ms.")]
        public int FunctionMs { get; set; }
        [Category("Levels"), Description("Digital level of the high-level guard tone (dBFS). Set Windows volume so the line reads 0 dBm.")]
        public double GuardDbfs { get; set; }
        [Category("Levels")] public double FunctionDbfs { get; set; }
        [Category("Levels"), Description("Low-level guard tone that stays under voice. The manual specifies 30 dB below the high-level guard tone.")]
        public double HoldDbfs { get; set; }
        [Category("Function tone")] public int MonitorHz { get; set; }
        [Category("Timing"), Description("Voice is gated on this long after PTT begins. Must be at least guard + function.")]
        public int VoiceDelayMs { get; set; }
        [Category("Guard tone")] public bool SingleTone { get; set; }
        [Category("Function tone")] public int FunctionAHz { get; set; }
        [Category("Function tone")] public int FunctionBHz { get; set; }
        [Category("Function tone")] public int FunctionCHz { get; set; }
        [Category("Timing"), Description("How long the low-level guard tone continues after the last voice sample. The consolette dekeys when it stops.")]
        public int TailMs { get; set; }
        [Category("Function tone"), Description("Standard tone table: 1150 Hz = secure (coded) select.")]
        public int SecureCodedHz { get; set; }
        [Category("Function tone"), Description("Standard tone table: 1050 Hz = secure clear select.")]
        public int SecureClearHz { get; set; }
        [Category("Levels"), Description("Voice peaks are limited to the high-level guard tone peak plus this many dB (-20 to +6). The TRC board's AGC uses the guard tone as its reference.")]
        public double VoicePeakDb { get; set; }
        public ToneProfile()
        {
            GuardHz = 2175; GuardMs = 120; FunctionMs = 40; GuardDbfs = -12; FunctionDbfs = -22; HoldDbfs = -42; MonitorHz = 2050; VoiceDelayMs = 200;
            FunctionAHz = 1950; FunctionBHz = 1850; FunctionCHz = 1750; TailMs = 120; SecureCodedHz = 1150; SecureClearHz = 1050;
        }
    }

    public class RadioProfile
    {
        [ReadOnly(true)] public string Id { get; set; }
        public string Name { get; set; }
        public string Model { get; set; }
        [Description("Informational label.")]
        public string ControlPath { get; set; }
        public string ControlHead { get; set; }
        [Browsable(false)] public string SerialPort { get; set; }
        [Category("Audio"), Description("Microphone (WinMM input) used for live transmit.")]
        [TypeConverter(typeof(InputDeviceConverter))]
        public string MicrophoneDevice { get; set; }
        [Category("Audio"), Description("Sound output wired through your isolation transformer to the consolette wireline (Accessory 2 LINE pins).")]
        [TypeConverter(typeof(OutputDeviceConverter))]
        public string PlannedTxOutput { get; set; }
        [Category("Audio"), Description("WholeDevice for a dedicated (USB) sound card. On an onboard 7.1 output choose the jack wired to the radio: Side (grey), Rear (black) or Center/Bass (orange); the other jacks get silence from this console. Set Windows speaker configuration to 7.1.")]
        public TxJack TxOutputJack { get; set; }
        [Category("Audio")] public int RxVolume { get; set; }
        [Category("Audio")] public int MicLevel { get; set; }
        [Category("Audio")] public int AlertLevel { get; set; }
        [Category("Audio")] public int ToneLevel { get; set; }
        [Category("Audio")] public bool MuteRxOnTx { get; set; }
        [Category("Audio")] public bool MuteMicOnRx { get; set; }
        [Category("Audio"), Description("Notch the guard-tone frequency out of microphone audio before it is sent (recommended).")]
        public bool MicNotch { get; set; }
        [Browsable(false)] public bool Muted { get; set; }
        [Browsable(false)] public int SelectedChannel { get; set; }
        [Category("Control"), Description("How PTT is sent: tone-remote guard/function tones (default), or the SB9600 front-panel PTT button.")]
        public PttMethod Ptt { get; set; }
        [Category("Control"), Description("How channels are selected: tone-remote function tone (8 modes only) or SB9600 head keys (any number of zones/channels; closed-loop using the radio display).")]
        public ChannelMethod ChannelSelect { get; set; }
        [Category("Control"), Description("Channel list index (0-based) that HOME selects. CPS import sets it to the radio's Home channel.")]
        public int HomeChannel { get; set; }
        [Browsable(false)] public List<ChannelProfile> Channels { get; set; }
        [Browsable(false)] public List<SoftKeyProfile> Softkeys { get; set; }
        [Browsable(false)] public ToneProfile Tone { get; set; }
        [Browsable(false)] public BusSettings Bus { get; set; }
        public RadioProfile()
        {
            Id = "R_" + Guid.NewGuid().ToString("N").ToUpperInvariant(); Name = "New radio"; Model = "Generic radio";
            ControlPath = "Tone remote + SB9600"; ControlHead = "W7"; SerialPort = "";
            MicrophoneDevice = ""; PlannedTxOutput = "";
            RxVolume = 50; MicLevel = 70; AlertLevel = 60; ToneLevel = 60; MuteRxOnTx = true; MuteMicOnRx = true; MicNotch = true;
            Ptt = PttMethod.TrcTones; ChannelSelect = ChannelMethod.TrcTone;
            Channels = new List<ChannelProfile>(); Softkeys = new List<SoftKeyProfile>(); Tone = new ToneProfile(); Bus = new BusSettings();
        }
        // Lists are intentionally empty after construction: XmlSerializer APPENDS to pre-filled lists,
        // which duplicated softkeys on every load in v0.4. Defaults are applied here after loading.
        public void EnsureDefaults()
        {
            if (Channels == null) Channels = new List<ChannelProfile>();
            if (Softkeys == null) Softkeys = new List<SoftKeyProfile>();
            if (Tone == null) Tone = new ToneProfile();
            if (Bus == null) Bus = new BusSettings();
            if (String.IsNullOrEmpty(Bus.PortName) && !String.IsNullOrEmpty(SerialPort)) Bus.PortName = SerialPort;
            Bus.EnsureDefaults();
            var seen = new HashSet<string>(); var unique = new List<SoftKeyProfile>();
            foreach (SoftKeyProfile key in Softkeys) if (key != null && key.Label != null && seen.Add(key.Label)) unique.Add(key);
            Softkeys = unique;
            if (Softkeys.Count == 0)
            {
                // Mirrors the W-series top-row softkeys. Rename them to match your codeplug's softkey labels.
                for (int k = 1; k <= 6; k++) Softkeys.Add(new SoftKeyProfile("TOP " + k, "head:TOP" + k));
                Softkeys.Add(new SoftKeyProfile("HOME", "head:HOME")); Softkeys.Add(new SoftKeyProfile("SEL", "head:SEL"));
            }
            if (Channels.Count == 0) Channels.Add(new ChannelProfile());
        }
        public override string ToString() { return Name; }
    }

    public enum AlertKind { Steady, Beeper, HiLo, Sweep, TwoTone, Dtmf }
    public class AlertProfile
    {
        [Category("1. Key"), Description("Name on the key (and for quick keys: alert:NAME).")] public string Name { get; set; }
        [Category("1. Key"), Description("Show this alert / tone set as a key in the ALERT / TONE KEYS panel on the main screen.")] public bool ShowOnMain { get; set; }
        [Category("1. Key"), Description("Key colour as #RRGGBB, or empty for the theme's alert colour.")] public string ColorHex { get; set; }
        [Category("2. Tones"), Description("TwoTone = a paging tone set: tone A for ToneAMs, GapMs of silence, then tone B for ToneBMs. Steady / Beeper / HiLo / Sweep = alert tones. Dtmf = the Digits.")] public AlertKind Kind { get; set; }
        [Category("2. Tones"), Description("Tone A in Hz (TwoTone: the first tone; HiLo / Sweep: the low tone).")] public double FrequencyA { get; set; }
        [Category("2. Tones"), Description("Tone B in Hz (TwoTone: the second tone; HiLo / Sweep: the high tone).")] public double FrequencyB { get; set; }
        [Category("2. Tones"), Description("Length of Steady, Beeper, HiLo and Sweep alerts in ms.")] public int DurationMs { get; set; }
        [Category("2. Tones"), Description("TwoTone: tone A length in ms (Quick Call II is usually 1000).")] public int ToneAMs { get; set; }
        [Category("2. Tones"), Description("TwoTone: tone B length in ms (Quick Call II is usually 3000; long tone 8000).")] public int ToneBMs { get; set; }
        [Category("2. Tones"), Description("Silence between tone A and tone B in ms (0 for most Quick Call II decoders).")] public int GapMs { get; set; }
        [Category("2. Tones"), Description("Dtmf only: digits 0-9, A-D, * and #.")] public string Digits { get; set; }
        public AlertProfile() { Name = "Custom alert"; ShowOnMain = true; ColorHex = ""; FrequencyA = 1000; FrequencyB = 1500; DurationMs = 3000; ToneAMs = 1000; ToneBMs = 3000; GapMs = 100; Digits = "123"; }
        // A new paging tone set with the usual Quick Call II timing; enter the department's verified A and B tones.
        public static AlertProfile NewToneSet() { return new AlertProfile { Name = "NEW TONE SET", Kind = AlertKind.TwoTone, FrequencyA = 1000, FrequencyB = 1500, ToneAMs = 1000, ToneBMs = 3000, GapMs = 0, ShowOnMain = true }; }
        public override string ToString() { return Name; }
        public AlertProfile Copy() { return (AlertProfile)MemberwiseClone(); }
        [Browsable(false)] public int TotalMs { get { return Kind == AlertKind.TwoTone ? ToneAMs + GapMs + ToneBMs : Kind == AlertKind.Dtmf ? Math.Max(1, (Digits ?? "").Length) * (150 + GapMs) : DurationMs; } }
    }

    public class QuickKey
    {
        public string Label { get; set; }
        [Description("Action string. Examples: alert:ATTENTION, chan:2, trc:MON, head:HOME, func:SCAN, select:Radio name, stopall, sb:05 83 01 57")]
        public string Action { get; set; }
        [Description("Button colour as #RRGGBB, or empty for the theme default.")]
        public string ColorHex { get; set; }
        [Description("Optional hotkey, e.g. F5 or Ctrl+1.")]
        public string Hotkey { get; set; }
        public QuickKey() { Label = "KEY"; Action = "unassigned"; ColorHex = ""; Hotkey = ""; }
        public QuickKey(string label, string action, string color, string hotkey) { Label = label; Action = action; ColorHex = color; Hotkey = hotkey; }
        public override string ToString() { return Label + " -> " + Action; }
    }

    public class HotkeySettings
    {
        [Description("Hold-to-talk key. Write keys like F12, Ctrl+1, Shift+F12, Alt+P, PageUp, Escape, NumPad5. The same text appears on the button.")]
        public string Ptt { get; set; }
        public string GroupPtt { get; set; }
        public string StopAll { get; set; }
        public string ChannelUp { get; set; }
        public string ChannelDown { get; set; }
        public string NextRadio { get; set; }
        public string PreviousRadio { get; set; }
        [Description("Extra monitor key. Blank by default: the MONITOR quick key (F4) already runs monitor.")] public string Monitor { get; set; }
        public string ReplayLast { get; set; }
        public HotkeySettings()
        {
            Ptt = "F12"; GroupPtt = "Shift+F12"; StopAll = "Escape"; ChannelUp = "PageUp"; ChannelDown = "PageDown";
            NextRadio = "F11"; PreviousRadio = "F10"; Monitor = ""; ReplayLast = "F8";   // MON has its own quick key (F4)
        }
    }

    public class RecorderSettings
    {
        [Description("Minutes of recent activity kept in memory for instant recall.")]
        public int RecallMinutes { get; set; }
        [Description("Write every call (RX and TX) to a WAV file plus a CSV index per day.")]
        public bool DiskLogging { get; set; }
        [Description("Recordings older than this many days are deleted automatically (checked at start and every hour). 0 = keep forever.")]
        public int KeepDays { get; set; }
        [Description("In LIVE, start receive monitoring and RX recording automatically on the devices last chosen in Audio > Receive audio.")]
        public bool AutoStartRx { get; set; }
        public string LogFolder { get; set; }
        [Description("RX audio above this level (dBFS) opens a recording.")]
        public int ThresholdDb { get; set; }
        [Description("Recording stays open this long after activity stops.")]
        public int HangMs { get; set; }
        public RecorderSettings() { RecallMinutes = 10; DiskLogging = true; KeepDays = 90; AutoStartRx = true; LogFolder = ""; ThresholdDb = -45; HangMs = 1500; }
    }

    public class ThemeNameConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return true; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context) { return new StandardValuesCollection(UiTheme.Names); }
    }

    public class UiSettings
    {
        [TypeConverter(typeof(ThemeNameConverter)), Description("Colour theme. 'Custom' uses the Custom* colours.")]
        public string ThemeName { get; set; }
        public string CustomBack { get; set; }
        public string CustomPanel { get; set; }
        public string CustomAccent { get; set; }
        public string CustomText { get; set; }
        public string CustomWarn { get; set; }
        public string CustomGood { get; set; }
        public string CustomBad { get; set; }
        public string FontName { get; set; }
        [Description("Overall interface scale in percent (70-200).")] public int FontScale { get; set; }
        [Description("Button height in pixels at 100% scale.")] public int ButtonHeight { get; set; }
        public CardDensity Density { get; set; }
        public CardPlacement Placement { get; set; }
        [Description("Columns of radio cards (1-4).")] public int CardColumns { get; set; }
        [Description("Width in pixels of the radio card column when placed left.")] public int CardsWidth { get; set; }
        public bool ShowCards { get; set; }
        public bool ShowQuickKeys { get; set; }
        public bool ShowJournal { get; set; }
        [Description("Auto picks Compact on small screens (such as 1024x768 or 1366x768) and Full on large ones. Full or Compact forces one.")] public LayoutMode Layout { get; set; }
        [Description("Also show the W7 control head on the main screen (Full layout only). Otherwise it opens from Tools > W7 control head or by clicking the radio display.")] public bool ShowHeadPanel { get; set; }
        [Browsable(false), Description("Not used since v0.7 (the main screen has no tabs).")] public string HiddenTabs { get; set; }
        public int JournalHeight { get; set; }
        [Description("Quick keys per row.")] public int QuickKeyColumns { get; set; }
        public bool Use24Hour { get; set; }
        [Description("Show the splash screen (logo and department name from branding\\brand.ini) while the console loads.")] public bool ShowSplash { get; set; }
        public int ChannelKeyColumns { get; set; }
        [Browsable(false)] public int WindowX { get; set; }
        [Browsable(false)] public int WindowY { get; set; }
        [Browsable(false)] public int WindowW { get; set; }
        [Browsable(false)] public int WindowH { get; set; }
        [Browsable(false)] public bool Maximized { get; set; }
        [Browsable(false)] public string LastTab { get; set; }
        public UiSettings()
        {
            ThemeName = "Black & Gold"; CustomBack = "#0E0C0B"; CustomPanel = "#1C1816"; CustomAccent = "#D6A83A"; CustomText = "#F3E9D0"; CustomWarn = "#ECBA4A"; CustomGood = "#68D088"; CustomBad = "#C4222A"; ShowSplash = true;
            FontName = "Segoe UI"; FontScale = 100; ButtonHeight = 34; Density = CardDensity.Normal; Placement = CardPlacement.Left; CardColumns = 1; CardsWidth = 380;
            ShowCards = true; ShowQuickKeys = true; ShowJournal = true; HiddenTabs = ""; JournalHeight = 110; QuickKeyColumns = 8; Use24Hour = true; ChannelKeyColumns = 4;
            WindowX = -1; WindowY = -1; WindowW = 1440; WindowH = 900; Maximized = true; LastTab = "CONTROL"; Layout = LayoutMode.Auto; ShowHeadPanel = false;
        }
        public UiSettings Copy()
        {
            var serializer = new XmlSerializer(typeof(UiSettings));
            using (var stream = new MemoryStream()) { serializer.Serialize(stream, this); stream.Position = 0; return (UiSettings)serializer.Deserialize(stream); }
        }
        public static bool ValidHex(string hex)
        {
            if (String.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return false;
            int n; return Int32.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n);
        }
        public static Color ParseHex(string hex, Color fallback)
        {
            if (!ValidHex(hex)) return fallback;
            int n = Int32.Parse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(255, (n >> 16) & 255, (n >> 8) & 255, n & 255);
        }
        public static string ToHex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
        public void Validate()
        {
            if (FontScale < 70 || FontScale > 200) throw new InvalidDataException("Interface scale must be 70-200%.");
            if (ButtonHeight < 24 || ButtonHeight > 64) throw new InvalidDataException("Button height must be 24-64.");
            if (CardColumns < 1 || CardColumns > 4) throw new InvalidDataException("Card columns must be 1-4.");
            if (CardsWidth < 200 || CardsWidth > 1000) throw new InvalidDataException("Card column width must be 200-1000.");
            if (JournalHeight < 40 || JournalHeight > 600) throw new InvalidDataException("Journal height must be 40-600.");
            if (QuickKeyColumns < 1 || QuickKeyColumns > 16) throw new InvalidDataException("Quick keys per row must be 1-16.");
            if (ChannelKeyColumns < 1 || ChannelKeyColumns > 10) throw new InvalidDataException("Channel key columns must be 1-10.");
            foreach (string hex in new string[] { CustomBack, CustomPanel, CustomAccent, CustomText, CustomWarn, CustomGood, CustomBad })
                if (!ValidHex(hex)) throw new InvalidDataException("Theme colours must look like #RRGGBB.");
            if (String.IsNullOrWhiteSpace(FontName) || FontName.Length > 60) throw new InvalidDataException("Font name is required.");
            if (HiddenTabs == null) HiddenTabs = "";
            if (ThemeName == null) ThemeName = "Black & Gold";
        }
    }

    public class LayoutPreset
    {
        public string Name { get; set; }
        public UiSettings Ui { get; set; }
        public LayoutPreset() { Name = "Layout"; Ui = new UiSettings(); }
        public override string ToString() { return Name; }
    }

    public class DispatchConfig
    {
        [ReadOnly(true)] public int Version { get; set; }
        public string ConsoleName { get; set; }
        public string OperatorName { get; set; }
        public int PttTimeoutSeconds { get; set; }
        public bool CompactCards { get; set; }
        public bool ConfirmLiveTx { get; set; }
        [TypeConverter(typeof(OutputDeviceConverter)), Description("Operator speakers/headphones for alert audition and instant-recall playback. Never connect this to a radio.")]
        public string OperatorOutputDevice { get; set; }
        [Browsable(false)] public UiSettings Ui { get; set; }
        [Browsable(false)] public HotkeySettings Hotkeys { get; set; }
        [Browsable(false)] public RecorderSettings Recorder { get; set; }
        [Browsable(false)] public FootswitchSettings Footswitch { get; set; }
        [Category("Operation"), Description("Mode the console starts in. Live = normal dispatch. ReceiveOnly = SB9600 and receive audio, nothing transmitted. Test = simulated radio, nothing sent to the hardware. Change it while running in Settings > Operating mode.")]
        public StartupMode StartIn { get; set; }
        [Category("Operation"), Description("Bench testing: require pressing ARM TX before anything is transmitted. Off = in Live the console is ready to talk as soon as the radio audio is open (PTT must still be held; STOP ALL and the PTT timeout always apply).")]
        public bool RequireArm { get; set; }
        [Browsable(false)] public List<RadioProfile> Radios { get; set; }
        [Browsable(false)] public List<AlertProfile> Alerts { get; set; }
        [Browsable(false)] public List<QuickKey> QuickKeys { get; set; }
        [Browsable(false)] public List<LayoutPreset> Layouts { get; set; }
        public DispatchConfig()
        {
            Version = 8; StartIn = StartupMode.Live; RequireArm = false; Footswitch = new FootswitchSettings(); ConsoleName = Brand.Department; OperatorName = "Dispatch"; PttTimeoutSeconds = 60; ConfirmLiveTx = true; OperatorOutputDevice = "";
            Ui = new UiSettings(); Hotkeys = new HotkeySettings(); Recorder = new RecorderSettings();
            Radios = new List<RadioProfile>(); Alerts = new List<AlertProfile>(); QuickKeys = new List<QuickKey>(); Layouts = new List<LayoutPreset>();
        }
        [XmlIgnore] public string SeedNote = "";
        public static DispatchConfig Load(string path) { return Load(path, Path.GetDirectoryName(path)); }
        // Default settings as on a first run (the shipped codeplug and quick keys when programDir has them).
        public static DispatchConfig Fresh(string programDir) { DispatchConfig fresh = Seed(programDir); fresh.Normalize(); return fresh; }
        // programDir: where the shipped first-run files are (cps, resources.map, channels.map) when the settings file
        // lives in the shared settings folder.
        public static DispatchConfig Load(string path, string programDir)
        {
            if (!File.Exists(path)) { DispatchConfig fresh = Seed(programDir); fresh.Normalize(); return fresh; }
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16000000 };
            using (var reader = XmlReader.Create(path, settings))
            {
                var config = (DispatchConfig)new XmlSerializer(typeof(DispatchConfig)).Deserialize(reader);
                config.MigrateV061(programDir);
                config.Normalize(); config.Validate(); return config;
            }
        }
        // v0.6.1 stored codeplug channels with FunctionHz 0 and Manual channel select (and no home channel). Re-apply the
        // shipped CPS export with the example defaults, keeping every other radio setting (COM port, audio, head buttons...).
        private void MigrateV061(string directory)
        {
            if (Radios == null) return;
            foreach (RadioProfile radio in Radios)
            {
                if (radio == null || radio.Channels == null || !radio.Channels.Exists(c => c.LegacyV061)) continue;
                string note = "";
                try { note = CpsImport.ApplyShipped(radio, directory); } catch (Exception) { note = ""; }
                if (note.Length == 0)
                {
                    foreach (ChannelProfile c in radio.Channels) { if (c.FunctionHz == 0) c.FunctionHz = 1950; c.LegacyV061 = false; }
                    int home = radio.Channels.FindIndex(c => c.ZoneNumber == 1 && c.ZonePosition == 2); radio.HomeChannel = home < 0 ? 0 : home;
                    if (radio.ChannelSelect == ChannelMethod.Manual) radio.ChannelSelect = ChannelMethod.Sb9600ModeKeys;
                    note = "kept the stored channel list (cps folder not found); PTT tone set to 1950 Hz (No Mode Select).";
                }
                SeedNote = (SeedNote ?? "") + radio.Name + ": v0.6.1 settings converted - " + note + " ";
            }
        }
        // SCAN and NUIS DEL as quick keys, right after MONITOR, unless a key already runs them. F5 / F6 only when free.
        internal void AddFunctionQuickKeys()
        {
            string[,] keys = { { "SCAN", "func:SCAN", "F5" }, { "NUIS DEL", "func:NUIS DEL", "F6" } };
            int at = QuickKeys.FindIndex(q => q != null && (q.Action == "func:MON" || q.Action == "trc:MON"));
            at = at < 0 ? QuickKeys.Count : at + 1;
            for (int i = 0; i < keys.GetLength(0); i++)
            {
                string action = keys[i, 1], hot = keys[i, 2];
                if (QuickKeys.Exists(q => q != null && String.Equals((q.Action ?? "").Trim(), action, StringComparison.OrdinalIgnoreCase))) continue;
                System.Windows.Forms.Keys k = Hotkey.Parse(hot);
                bool taken = QuickKeys.Exists(q => q != null && Hotkey.Parse(q.Hotkey) == k);
                if (Hotkeys != null) foreach (string g in new string[] { Hotkeys.Ptt, Hotkeys.GroupPtt, Hotkeys.StopAll, Hotkeys.ChannelUp, Hotkeys.ChannelDown, Hotkeys.NextRadio, Hotkeys.PreviousRadio, Hotkeys.Monitor, Hotkeys.ReplayLast }) if (Hotkey.Parse(g) == k) taken = true;
                QuickKeys.Insert(Math.Min(at, QuickKeys.Count), new QuickKey(keys[i, 0], action, "", taken ? "" : hot)); at++;
            }
        }
        // Fills anything an older (v0.4) file did not contain and removes load-time duplicates.
        public void Normalize()
        {
            if (Ui == null) Ui = new UiSettings();
            if (Hotkeys == null) Hotkeys = new HotkeySettings();
            if (Recorder == null) Recorder = new RecorderSettings();
            if (Footswitch == null) Footswitch = new FootswitchSettings();
            if (Radios == null) Radios = new List<RadioProfile>();
            if (Alerts == null) Alerts = new List<AlertProfile>();
            if (QuickKeys == null) QuickKeys = new List<QuickKey>();
            if (Layouts == null) Layouts = new List<LayoutPreset>();
            if (OperatorOutputDevice == null) OperatorOutputDevice = "";
            // A theme name this build does not know (renamed or from another build) falls back to the default theme.
            if (Ui.ThemeName == null || Array.IndexOf(UiTheme.Names, Ui.ThemeName) < 0) Ui.ThemeName = UiTheme.Names[0];
            foreach (LayoutPreset preset in Layouts) if (preset != null && preset.Ui != null && (preset.Ui.ThemeName == null || Array.IndexOf(UiTheme.Names, preset.Ui.ThemeName) < 0)) preset.Ui.ThemeName = UiTheme.Names[0];
            if (String.IsNullOrWhiteSpace(ConsoleName)) ConsoleName = Brand.Department;
            foreach (RadioProfile radio in Radios) if (radio != null) { radio.EnsureDefaults(); CpsImport.ApplyW7BenchKeymap(radio); }
            foreach (QuickKey q in QuickKeys) if (q != null && q.Label == "MONITOR" && q.Action == "trc:MON" && q.Hotkey == "F4") q.Action = "func:MON";   // v0.7 seed: SB9600 first
            if (QuickKeys.Count == 0)
            {
                QuickKeys.Add(new QuickKey("ATTENTION", "alert:ATTENTION", "#694C23", "F1")); QuickKeys.Add(new QuickKey("BEEPER", "alert:BEEPER", "#694C23", "F2"));
                QuickKeys.Add(new QuickKey("HI-LO", "alert:HI-LO", "#694C23", "F3")); QuickKeys.Add(new QuickKey("MONITOR", "trc:MON", "", "F4"));
                QuickKeys.Add(new QuickKey("CH 1", "chan:1", "", "Ctrl+1")); QuickKeys.Add(new QuickKey("CH 2", "chan:2", "", "Ctrl+2"));
                QuickKeys.Add(new QuickKey("CH 3", "chan:3", "", "Ctrl+3")); QuickKeys.Add(new QuickKey("CH 4", "chan:4", "", "Ctrl+4"));
                AddFunctionQuickKeys();
            }
            // v0.8.1: the radio-function buttons left the main screen; their functions live on the quick-key bar (one button per control).
            if (Version < 6)
            {
                AddFunctionQuickKeys();
                bool monKey = QuickKeys.Exists(q => q != null && (q.Action == "func:MON" || q.Action == "trc:MON"));
                if (monKey && Hotkeys.Monitor == "F9") Hotkeys.Monitor = "";   // MON already has its quick key (F4)
                Version = 6;
            }
            // v0.9: recordings are kept (RX and TX, 90 days by default), so disk logging is turned on once.
            if (Version < 7) { Recorder.DiskLogging = true; Version = 7; }
            // v1.0: alerts choose whether they have a main-screen key. Keep what older versions showed (the first 8),
            // without the two test alerts.
            if (Version < 8)
            {
                for (int i = 0; i < Alerts.Count; i++) if (Alerts[i] != null) Alerts[i].ShowOnMain = i < 8 && Alerts[i].Name != "TWO-TONE TEST" && Alerts[i].Name != "DTMF TEST";
                Version = 8;
            }
            foreach (AlertProfile a in Alerts) if (a != null && a.ColorHex == null) a.ColorHex = "";
            if (Layouts.Count == 0)
            {
                var standard = new UiSettings { ThemeName = "Black & Gold" };
                var wide = new UiSettings { ThemeName = "Night Blue" };
                var big = new UiSettings { ThemeName = "High Contrast", FontScale = 130, ButtonHeight = 44, Density = CardDensity.Large };
                var compact = new UiSettings { ThemeName = "Day Light", FontScale = 90, ButtonHeight = 30, Density = CardDensity.Compact, CardColumns = 2, CardsWidth = 520 };
                Layouts.Add(new LayoutPreset { Name = "Standard (black & gold)", Ui = standard });
                Layouts.Add(new LayoutPreset { Name = "Standard (night blue)", Ui = wide });
                Layouts.Add(new LayoutPreset { Name = "Large print (high contrast)", Ui = big });
                Layouts.Add(new LayoutPreset { Name = "Compact two-column (day)", Ui = compact });
            }
        }
        public void Validate()
        {
            if (Radios == null || Radios.Count < 1 || Radios.Count > 32) throw new InvalidDataException("Configure between 1 and 32 radio resources.");
            if (Alerts == null || Alerts.Count < 1 || Alerts.Count > 64) throw new InvalidDataException("Configure between 1 and 64 alerts.");
            if (PttTimeoutSeconds < 5 || PttTimeoutSeconds > 180) throw new InvalidDataException("PTT timeout must be 5-180 seconds.");
            if (QuickKeys.Count > 64) throw new InvalidDataException("At most 64 quick keys.");
            if (Layouts.Count > 32) throw new InvalidDataException("At most 32 saved layouts.");
            if (Recorder.RecallMinutes < 1 || Recorder.RecallMinutes > 120) throw new InvalidDataException("Recall minutes must be 1-120.");
            if (Recorder.ThresholdDb < -90 || Recorder.ThresholdDb > -5) throw new InvalidDataException("Recorder threshold must be -90 to -5 dBFS.");
            if (Recorder.HangMs < 100 || Recorder.HangMs > 30000) throw new InvalidDataException("Recorder hang time must be 100-30000 ms.");
            if (Recorder.KeepDays < 0 || Recorder.KeepDays > 3650) throw new InvalidDataException("Recordings: keep 0-3650 days (0 = forever).");
            if (Footswitch.ControllerNumber < 1 || Footswitch.ControllerNumber > 16 || Footswitch.Button < 1 || Footswitch.Button > 32) throw new InvalidDataException("Footswitch: controller 1-16, button 1-32.");
            if (Footswitch.PortName == null) Footswitch.PortName = "";
            if (Footswitch.Key == null) Footswitch.Key = "";
            if (Footswitch.Source == FootswitchSource.KeyboardKey)
            {
                System.Windows.Forms.Keys pedal = Hotkey.Parse(Footswitch.Key);
                if (pedal == System.Windows.Forms.Keys.None || (pedal & System.Windows.Forms.Keys.Modifiers) != 0) throw new InvalidDataException("Footswitch key must be one key with no Ctrl/Shift/Alt (for example F13, Scroll, Pause).");
                foreach (string g in new string[] { Hotkeys.StopAll, Hotkeys.ChannelUp, Hotkeys.ChannelDown })
                    if (Hotkey.Parse(g) == pedal) throw new InvalidDataException("The footswitch key " + Footswitch.Key + " is also a console hotkey; pick a key nothing else uses.");
            }
            if (Footswitch.Source == FootswitchSource.ComPortLine && Footswitch.PortName.Length > 0 && Radios.Exists(r => r != null && r.Bus != null && String.Equals(r.Bus.PortName, Footswitch.PortName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The footswitch needs its own COM port; " + Footswitch.PortName + " is the RIB port.");
            Ui.Validate();
            foreach (LayoutPreset layout in Layouts) { if (layout == null || String.IsNullOrWhiteSpace(layout.Name) || layout.Ui == null) throw new InvalidDataException("Invalid layout preset."); layout.Ui.Validate(); }
            foreach (QuickKey key in QuickKeys)
            {
                if (key == null || String.IsNullOrWhiteSpace(key.Label) || key.Label.Length > 24 || key.Action == null || key.Action.Length > 200) throw new InvalidDataException("Quick keys need a label (1-24 characters) and an action.");
                if (!String.IsNullOrEmpty(key.ColorHex) && !UiSettings.ValidHex(key.ColorHex)) throw new InvalidDataException("Quick key colours must be #RRGGBB.");
            }
            var ids = new HashSet<string>();
            foreach (RadioProfile radio in Radios)
            {
                if (!RadioResources.ValidId(radio.Id) || !ids.Add(radio.Id)) throw new InvalidDataException("Radio IDs must be unique and contain only A-Z, 0-9 and underscore.");
                if (String.IsNullOrWhiteSpace(radio.Name) || radio.Name.Length > 60) throw new InvalidDataException("Radio names must be 1-60 characters.");
                if (radio.Channels == null || radio.Channels.Count < 1 || radio.Channels.Count > 512) throw new InvalidDataException("Each radio needs 1-512 configured channels.");
                if (radio.Softkeys == null || radio.Softkeys.Count > 24 || radio.Tone == null || radio.Bus == null) throw new InvalidDataException("Invalid softkeys, bus or tone profile.");
                foreach (SoftKeyProfile key in radio.Softkeys)
                    if (key == null || String.IsNullOrWhiteSpace(key.Label) || key.Label.Length > 30 || key.Binding == null) throw new InvalidDataException("Softkeys need a label (1-30 characters) and binding text.");
                if (radio.RxVolume < 0 || radio.RxVolume > 100 || radio.MicLevel < 0 || radio.MicLevel > 100 || radio.AlertLevel < 0 || radio.AlertLevel > 100 || radio.ToneLevel < 0 || radio.ToneLevel > 100) throw new InvalidDataException("Mixer values must be 0-100.");
                foreach (ChannelProfile channel in radio.Channels)
                    if (String.IsNullOrWhiteSpace(channel.Name) || String.IsNullOrWhiteSpace(channel.Zone) || channel.FunctionHz < 20 || channel.FunctionHz > 5000 || channel.CtcssHz < 0 || channel.CtcssHz > 300) throw new InvalidDataException("Invalid channel label, function frequency or CTCSS value.");
                radio.SelectedChannel = Math.Max(0, Math.Min(radio.Channels.Count - 1, radio.SelectedChannel));
                ToneProfile t = radio.Tone;
                foreach (double n in new double[] { t.GuardHz, t.GuardDbfs, t.FunctionDbfs, t.HoldDbfs })
                    if (Double.IsNaN(n) || Double.IsInfinity(n)) throw new InvalidDataException("Tone values must be finite numbers.");
                foreach (ChannelProfile channel in radio.Channels)
                    if (Double.IsNaN(channel.CtcssHz) || Double.IsInfinity(channel.CtcssHz)) throw new InvalidDataException("CTCSS must be a finite number.");
                if (t.GuardHz < 20 || t.GuardHz > 5000 || t.GuardMs < 1 || t.GuardMs > 5000 || t.FunctionMs < 1 || t.FunctionMs > 5000 || t.VoiceDelayMs < 0 || t.VoiceDelayMs > 10000 || t.TailMs < 0 || t.TailMs > 5000 || Double.IsNaN(t.VoicePeakDb) || t.VoicePeakDb < -20 || t.VoicePeakDb > 6 || t.GuardDbfs < -90 || t.GuardDbfs > -1 || t.FunctionDbfs < -90 || t.FunctionDbfs > -1 || t.HoldDbfs < -90 || t.HoldDbfs > -1 || t.MonitorHz < 20 || t.MonitorHz > 5000 || t.FunctionAHz < 20 || t.FunctionAHz > 5000 || t.FunctionBHz < 20 || t.FunctionBHz > 5000 || t.FunctionCHz < 20 || t.FunctionCHz > 5000 || t.SecureCodedHz < 20 || t.SecureCodedHz > 5000 || t.SecureClearHz < 20 || t.SecureClearHz > 5000) throw new InvalidDataException("Tone frequencies must be 20-5000 Hz; levels -90 to -1 dBFS; check timing values.");
                BusSettings b = radio.Bus;
                if (b.IndicatorScan < -1 || b.IndicatorScan > 255) b.IndicatorScan = -1; if (b.IndicatorMonitor < -1 || b.IndicatorMonitor > 255) b.IndicatorMonitor = -1;
                if (b.IndicatorDirect < -1 || b.IndicatorDirect > 255) b.IndicatorDirect = -1; if (b.IndicatorLowPower < -1 || b.IndicatorLowPower > 255) b.IndicatorLowPower = -1; if (b.IndicatorSecure < -1 || b.IndicatorSecure > 255) b.IndicatorSecure = -1;
                if (b.PortName == null || b.PortName.Length > 40 || b.Baud < 1200 || b.Baud > 115200 || b.ChannelBase < 0 || b.ChannelBase > 1) throw new InvalidDataException("Invalid SB9600 bus settings.");
                if (b.HeadButtons == null || b.HeadButtons.Count > 128 || b.Rules == null || b.Rules.Count > 128) throw new InvalidDataException("Too many head buttons or bus rules.");
                if (b.DisplayLookups == null || b.DisplayLookups.Count > 256) throw new InvalidDataException("At most 256 display lookups per radio.");
                foreach (TextLookup l in b.DisplayLookups) if (l == null || (l.Match ?? "").Length > 40 || (l.Replace ?? "").Length > 60) throw new InvalidDataException("Display lookups: match up to 40 characters, replacement up to 60.");
                if (radio.HomeChannel < 0 || radio.HomeChannel >= radio.Channels.Count) radio.HomeChannel = 0;
                foreach (ChannelProfile channel in radio.Channels)
                {
                    if (channel.RxTone == null) channel.RxTone = ""; if (channel.TxTone == null) channel.TxTone = "";
                    if (channel.BusMode < -1 || channel.BusMode > 255) channel.BusMode = -1;
                    if (Double.IsNaN(channel.RxMHz) || Double.IsNaN(channel.TxMHz) || channel.RxMHz < 0 || channel.RxMHz > 1000 || channel.TxMHz < 0 || channel.TxMHz > 1000 || channel.ZoneNumber < 0 || channel.ZoneNumber > 255 || channel.ZonePosition < 0 || channel.ZonePosition > 255 || channel.ScanPriority < 0 || channel.ScanPriority > 2) throw new InvalidDataException("Invalid codeplug fields on channel " + channel.Name + ".");
                }
                foreach (ChannelProfile channel in radio.Channels) { if (channel.RadioText == null) channel.RadioText = ""; if (channel.RadioText.Length > 32) throw new InvalidDataException("Channel radio text must be 32 characters or fewer."); }
                foreach (HeadButton hb in b.HeadButtons) if (hb == null || String.IsNullOrWhiteSpace(hb.Name) || hb.Name.Length > 30 || hb.Code < 0 || hb.Code > 255) throw new InvalidDataException("Head buttons need a name and a code 0-255.");
                foreach (BusRule rule in b.Rules) { byte[] p, m; if (rule == null || String.IsNullOrWhiteSpace(rule.Name) || !SbPattern.TryParse(rule.Pattern, out p, out m)) throw new InvalidDataException("Bus rule patterns must be four hex bytes (** = any)."); }
            }
            foreach (AlertProfile alert in Alerts)
                if (Double.IsNaN(alert.FrequencyA) || Double.IsNaN(alert.FrequencyB) || Double.IsInfinity(alert.FrequencyA) || Double.IsInfinity(alert.FrequencyB) || String.IsNullOrWhiteSpace(alert.Name) || alert.FrequencyA < 20 || alert.FrequencyA > 5000 || alert.FrequencyB < 20 || alert.FrequencyB > 5000 || alert.DurationMs < 50 || alert.DurationMs > 30000 || alert.ToneAMs < 10 || alert.ToneAMs > 15000 || alert.ToneBMs < 10 || alert.ToneBMs > 15000 || alert.GapMs < 0 || alert.GapMs > 3000 || alert.TotalMs > 60000 || (alert.Digits ?? "").Length > 32 || !ConsoleWave.ValidDigits(alert.Digits ?? "") || alert.Name.Length > 30 || (!String.IsNullOrEmpty(alert.ColorHex) && !UiSettings.ValidHex(alert.ColorHex))) throw new InvalidDataException("Invalid alert / tone set (" + alert.Name + "). Use a name up to 30 characters, 20-5000 Hz, bounded durations, DTMF digits 0-9, A-D, *, # and colours as #RRGGBB.");
        }
        public void Save(string path)
        {
            Normalize(); Validate(); string temp = path + ".tmp";
            using (var writer = new StreamWriter(temp)) new XmlSerializer(typeof(DispatchConfig)).Serialize(writer, this);
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
        }
        public DispatchConfig Copy()
        {
            var serializer = new XmlSerializer(typeof(DispatchConfig));
            using (var stream = new MemoryStream()) { serializer.Serialize(stream, this); stream.Position = 0; var copy = (DispatchConfig)serializer.Deserialize(stream); copy.Normalize(); return copy; }
        }
        public static RadioProfile NewRadio(string name)
        {
            var radio = new RadioProfile { Name = name }; radio.Channels.Add(new ChannelProfile()); radio.EnsureDefaults(); return radio;
        }
        public static void AddDefaultAlerts(List<AlertProfile> alerts)
        {
            alerts.Add(new AlertProfile { Name = "ATTENTION", Kind = AlertKind.Steady, DurationMs = 2000 });
            alerts.Add(new AlertProfile { Name = "BEEPER", Kind = AlertKind.Beeper });
            alerts.Add(new AlertProfile { Name = "HI-LO", Kind = AlertKind.HiLo, FrequencyA = 800, FrequencyB = 1200 });
            alerts.Add(new AlertProfile { Name = "SWEEP", Kind = AlertKind.Sweep, FrequencyA = 600, FrequencyB = 1400, DurationMs = 4000 });
            alerts.Add(new AlertProfile { Name = "TWO-TONE TEST", Kind = AlertKind.TwoTone, FrequencyA = 600, FrequencyB = 1000, ShowOnMain = false });
            alerts.Add(new AlertProfile { Name = "DTMF TEST", Kind = AlertKind.Dtmf, Digits = "123#", ShowOnMain = false });
        }
        private static DispatchConfig Seed(string directory)
        {
            var config = new DispatchConfig();
            List<RadioResource> previous = RadioResources.Load(Path.Combine(directory, "resources.map"));
            // Standard TRC tone table (XTL5000 consolette manual, Table 3-1): modes 1-8.
            int[] frequencies = { 1950, 1850, 1350, 1250, 1550, 1450, 1750, 1650 };
            foreach (RadioResource resource in previous)
            {
                var radio = new RadioProfile { Id = resource.Id, Name = resource.Name, Model = resource.XtlTonePreview ? "XTL5000 consolette / PLN1687 TRC" : "Generic radio", ControlHead = resource.XtlTonePreview ? "W7" : "Unassigned" };
                for (int i = 0; i < 8; i++) radio.Channels.Add(new ChannelProfile { Name = "MODE " + (i + 1), FunctionHz = frequencies[i] });
                config.Radios.Add(radio);
            }
            string map = Path.Combine(directory, "channels.map");
            if (File.Exists(map)) foreach (string line in File.ReadAllLines(map))
            {
                string[] parts = line.Split('|'); int number;
                if (parts.Length == 3 && int.TryParse(parts[1], out number) && number > 0 && number <= 8)
                    foreach (RadioProfile radio in config.Radios) if (radio.Id == parts[0]) radio.Channels[number - 1].Name = parts[2];
            }
            AddDefaultAlerts(config.Alerts);
            try
            {
                RadioProfile xtl = config.Radios.FirstOrDefault(r => r.ControlHead == "W7") ?? config.Radios.FirstOrDefault();
                config.SeedNote = CpsImport.ApplyShipped(xtl, directory);
                if (config.SeedNote.Length > 0)
                {
                    // First-run quick keys: the three alert tones, monitor, and the first four channels of zone 1 (Ctrl+1..4).
                    config.QuickKeys.Add(new QuickKey("ATTENTION", "alert:ATTENTION", "#694C23", "F1")); config.QuickKeys.Add(new QuickKey("BEEPER", "alert:BEEPER", "#694C23", "F2"));
                    config.QuickKeys.Add(new QuickKey("HI-LO", "alert:HI-LO", "#694C23", "F3")); config.QuickKeys.Add(new QuickKey("MONITOR", "func:MON", "", "F4"));
                    for (int i = 1; i <= 4; i++)
                    {
                        ChannelProfile first = xtl.Channels.FirstOrDefault(c => c.ZoneNumber == 1 && c.ZonePosition == i);
                        if (first != null) config.QuickKeys.Add(new QuickKey((first.RadioText ?? "").Trim().Length > 0 ? first.RadioText.Trim() : first.Name, "chan:Z1/" + i, "", "Ctrl+" + i));
                    }
                    config.AddFunctionQuickKeys();
                }
            }
            catch (Exception ex) { config.SeedNote = "The shipped CPS channel file could not be loaded: " + ex.Message; }
            return config;
        }
    }

    // Type converters that give PropertyGrid drop-downs for audio devices and COM ports while still allowing typed text.
    public class ComPortConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var list = new List<string>(); list.Add("");
            try { list.AddRange(System.IO.Ports.SerialPort.GetPortNames()); } catch (Exception) { }
            return new StandardValuesCollection(list);
        }
    }
    public class InputDeviceConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var list = new List<string>(); list.Add("");
            try { foreach (AudioDevice d in ReceiveAudio.Devices(true)) list.Add(d.Name); } catch (Exception) { }
            return new StandardValuesCollection(list);
        }
    }
    public class OutputDeviceConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var list = new List<string>(); list.Add("");
            try { foreach (AudioDevice d in ReceiveAudio.Devices(false)) list.Add(d.Name); } catch (Exception) { }
            return new StandardValuesCollection(list);
        }
    }

    internal sealed class RadioState
    {
        public bool Scan, Monitor, Direct, LowPower, Secure, Rx, Emergency, Beacon, Group;
        // Channel the radio display shows when it is not the operator's channel (scan landing): shown at the top only.
        public int RxChannel = -1;
        // Last channel the display named while receiving or scanning, and when (local time): labels RX recordings.
        public int LastRxChannel = -1;
        public DateTime LastRxAt = DateTime.MinValue;
        // Keyed-but-dropped watch (TRC PTT in Live): the radio's own SB9600 transmit report during a console transmission.
        public bool TxSeenThisKey, RadioDropped, DropLogged, NoKeyLogged;
        // Set by Sync radio status: the next channel the radio display names becomes the console's channel (start-up).
        public bool SyncAdopt;
        public DateTime TxDropAt = DateTime.MaxValue, KeyedAt = DateTime.MaxValue;
        public bool BusTx, BusRx, BusAudioOpen;
        public string Caller = "--", LastAction = "READY", Keypad = "", DisplayMessage = "";
        public string BusDisplay = "", BusDisplay2 = "";
        public readonly Dictionary<int, int> Indicators = new Dictionary<int, int>();
        public int BusChannel = -1;
        public DateTime LastBusFrame = DateTime.MinValue;
        public int DisplaySeq;            // +1 for every display message from the radio: a fresh readback for the channel seek
        public bool DirectGotoFailed;     // the radio ignored or mis-handled direct channel select this session
        public int OffsetCandidate = Int32.MinValue, OffsetHits;
        // Latching radio states (MON, SCAN, DIR, LPWR, SECURE) the radio has actually reported since the bus opened.
        // The XTL reports a light only when it changes, so until then the console shows it as unknown ("?").
        public readonly HashSet<string> Known = new HashSet<string>();
        // Counters the command checks watch for the radio's answer: key beep, monitor report, scan light, any light, TX.
        public int BeepSeq, MonSeq, ScanSeq, IndSeq, TxOnSeq;   // mode-report learning: the same offset seen twice is trusted
        public bool MenuOpen;
        public int MenuIndex;
    }
    internal sealed class TxController
    {
        public readonly List<string> Targets = new List<string>();
        public bool Active { get { return Targets.Count > 0; } }
        public DateTime Started { get; private set; }
        public DateTime Deadline { get; private set; }
        public string Reason { get; private set; }
        public void Start(IEnumerable<string> ids, string reason, DateTime now, int timeoutSeconds, int durationMs)
        {
            if (Active) throw new InvalidOperationException("A transmit action is already active.");
            var unique = new HashSet<string>(ids); if (unique.Count == 0) throw new InvalidOperationException("Select at least one resource.");
            Targets.AddRange(unique); Reason = reason; Started = now;
            Deadline = now.AddMilliseconds(durationMs > 0 ? Math.Min(durationMs, timeoutSeconds * 1000) : timeoutSeconds * 1000);
        }
        public bool Expired(DateTime now) { return Active && now >= Deadline; }
        public void Stop() { Targets.Clear(); Reason = ""; }
    }
    internal sealed class PageJob
    {
        public readonly string RadioId, RadioName;
        public readonly int ChannelIndex;
        public readonly string ChannelName;
        public readonly AlertProfile Alert;
        public PageJob(RadioProfile radio, AlertProfile alert) { RadioId = radio.Id; RadioName = radio.Name; ChannelIndex = radio.SelectedChannel; ChannelName = radio.Channels[ChannelIndex].Name; Alert = alert.Copy(); }
        public override string ToString() { return RadioName + " / " + ChannelName + " / " + Alert.Name; }
    }
}
