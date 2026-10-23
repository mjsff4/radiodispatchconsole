using System;
using System.Drawing;
using System.Windows.Forms;

// Colour themes, scaling and small control factories so every part of the UI follows the operator's choices.
namespace RadioDispatchConsole
{
    internal sealed class UiTheme : IDisposable
    {
        public static readonly string[] Names = { "Black & Gold", "Night Blue", "Night Red (dark adapted)", "Day Light", "High Contrast", "Console Gray", "Classic Dispatch (MCC style)", "Custom" };
        public Color Back, Panel, Raised, Accent, Text, Dim, Warn, Good, Bad, Button, Border, Lcd, LcdText, Active, Selected;
        public Font Base, Bold, Small, Big, Huge, Mono, MonoBig, MonoMid;
        public double Scale;
        public int ButtonH;
        public string FontName;

        public static UiTheme Create(UiSettings ui)
        {
            var t = new UiTheme(); t.Scale = Math.Max(0.7, Math.Min(2.0, ui.FontScale / 100.0)); t.FontName = ui.FontName ?? "Segoe UI";
            string name = ui.ThemeName ?? "Black & Gold";
            if (name.StartsWith("Black & Gold"))
            {
                // Black field, gold trim, fire-red accents.
                t.Back = C(14, 12, 11); t.Panel = C(28, 24, 22); t.Raised = C(52, 36, 28); t.Accent = C(214, 168, 58); t.Text = C(243, 233, 208); t.Dim = C(168, 152, 120);
                t.Warn = C(236, 186, 74); t.Good = C(104, 208, 136); t.Bad = C(196, 34, 42); t.Button = C(74, 24, 26); t.Border = C(150, 114, 44); t.Lcd = C(6, 6, 6); t.LcdText = C(232, 186, 72); t.Active = C(168, 40, 38);
            }
            else if (name.StartsWith("Night Red"))
            {
                t.Back = C(16, 6, 6); t.Panel = C(30, 12, 12); t.Raised = C(46, 18, 18); t.Accent = C(220, 70, 60); t.Text = C(235, 150, 140); t.Dim = C(150, 80, 72);
                t.Warn = C(240, 140, 60); t.Good = C(200, 120, 60); t.Bad = C(255, 60, 50); t.Button = C(52, 20, 20); t.Border = C(90, 36, 34); t.Lcd = C(24, 4, 4); t.LcdText = C(255, 90, 70); t.Active = C(120, 40, 20);
            }
            else if (name == "Day Light")
            {
                t.Back = C(226, 231, 236); t.Panel = C(245, 247, 250); t.Raised = C(214, 228, 240); t.Accent = C(0, 102, 153); t.Text = C(20, 30, 40); t.Dim = C(90, 104, 118);
                t.Warn = C(176, 104, 0); t.Good = C(16, 128, 60); t.Bad = C(190, 30, 36); t.Button = C(220, 226, 233); t.Border = C(160, 172, 186); t.Lcd = C(198, 214, 196); t.LcdText = C(20, 40, 24); t.Active = C(255, 200, 90);
            }
            else if (name == "High Contrast")
            {
                t.Back = C(0, 0, 0); t.Panel = C(10, 10, 10); t.Raised = C(40, 40, 70); t.Accent = C(0, 255, 255); t.Text = C(255, 255, 255); t.Dim = C(200, 200, 200);
                t.Warn = C(255, 255, 0); t.Good = C(0, 255, 0); t.Bad = C(255, 40, 40); t.Button = C(30, 30, 30); t.Border = C(255, 255, 255); t.Lcd = C(0, 0, 0); t.LcdText = C(0, 255, 255); t.Active = C(160, 110, 0);
            }
            else if (name == "Console Gray")
            {
                t.Back = C(52, 56, 60); t.Panel = C(68, 72, 78); t.Raised = C(86, 92, 100); t.Accent = C(140, 200, 255); t.Text = C(236, 238, 240); t.Dim = C(170, 176, 182);
                t.Warn = C(255, 200, 80); t.Good = C(120, 220, 140); t.Bad = C(230, 80, 70); t.Button = C(84, 90, 98); t.Border = C(120, 128, 138); t.Lcd = C(20, 40, 30); t.LcdText = C(130, 255, 170); t.Active = C(150, 110, 40);
            }
            else if (name.StartsWith("Classic"))
            {
                // A conventional dispatch-console look: light grey panels and keys with dark text, a blue accent, an amber
                // selected channel, a light display with dark blue text.
                t.Back = C(146, 152, 158); t.Panel = C(200, 204, 208); t.Raised = C(216, 220, 224); t.Accent = C(0, 72, 150); t.Text = C(14, 18, 24); t.Dim = C(78, 84, 92);
                t.Warn = C(176, 98, 0); t.Good = C(0, 122, 48); t.Bad = C(196, 20, 20); t.Button = C(226, 228, 230); t.Border = C(104, 110, 118); t.Lcd = C(234, 238, 230); t.LcdText = C(0, 50, 118); t.Active = C(255, 206, 84);
            }
            else if (name == "Custom")
            {
                t.Back = UiSettings.ParseHex(ui.CustomBack, C(13, 21, 31)); t.Panel = UiSettings.ParseHex(ui.CustomPanel, C(25, 39, 54)); t.Accent = UiSettings.ParseHex(ui.CustomAccent, C(75, 203, 215));
                t.Text = UiSettings.ParseHex(ui.CustomText, C(228, 238, 245)); t.Warn = UiSettings.ParseHex(ui.CustomWarn, C(244, 190, 78)); t.Good = UiSettings.ParseHex(ui.CustomGood, C(104, 218, 156)); t.Bad = UiSettings.ParseHex(ui.CustomBad, C(192, 65, 68));
                t.Raised = Mix(t.Panel, t.Accent, 0.18); t.Dim = Mix(t.Text, t.Panel, 0.4); t.Button = Mix(t.Panel, t.Text, 0.12); t.Border = Mix(t.Panel, t.Text, 0.3);
                t.Lcd = Mix(t.Back, t.Accent, 0.08); t.LcdText = t.Accent; t.Active = Mix(t.Warn, t.Panel, 0.45);
            }
            else
            {
                t.Back = C(13, 21, 31); t.Panel = C(25, 39, 54); t.Raised = C(35, 63, 79); t.Accent = C(75, 203, 215); t.Text = C(228, 238, 245); t.Dim = C(140, 160, 176);
                t.Warn = C(244, 190, 78); t.Good = C(104, 218, 156); t.Bad = C(192, 65, 68); t.Button = C(40, 65, 83); t.Border = C(68, 100, 118); t.Lcd = C(8, 33, 40); t.LcdText = C(75, 203, 215); t.Active = C(115, 85, 37);
            }
            t.Selected = Mix(t.Raised, t.Accent, 0.25);
            t.ButtonH = (int)Math.Round(ui.ButtonHeight * t.Scale);
            t.Base = MakeFont(t.FontName, 9F * (float)t.Scale, FontStyle.Regular);
            t.Bold = MakeFont(t.FontName, 9F * (float)t.Scale, FontStyle.Bold);
            t.Small = MakeFont(t.FontName, 8F * (float)t.Scale, FontStyle.Regular);
            t.Big = MakeFont(t.FontName, 12F * (float)t.Scale, FontStyle.Bold);
            t.Huge = MakeFont(t.FontName, 20F * (float)t.Scale, FontStyle.Bold);
            t.Mono = MakeFont("Consolas", 9F * (float)t.Scale, FontStyle.Regular);
            t.MonoBig = MakeFont("Consolas", 26F * (float)t.Scale, FontStyle.Bold);
            t.MonoMid = MakeFont("Consolas", 17F * (float)t.Scale, FontStyle.Bold);
            return t;
        }
        private static Font MakeFont(string family, float size, FontStyle style)
        {
            try { return new Font(family, size, style); } catch (Exception) { return new Font(FontFamily.GenericSansSerif, size, style); }
        }
        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        public static Color ReadableOn(Color back) { return (back.R * 299 + back.G * 587 + back.B * 114) / 1000 > 140 ? Color.Black : Color.White; }
        public int S(int px) { return (int)Math.Round(px * Scale); }

        public Button MakeButton(string text, Action action, int width)
        {
            var b = new Button { Text = text, Width = S(width), Height = ButtonH, Margin = new Padding(S(3)), Font = Bold, UseMnemonic = false };
            StyleButton(b, Button);
            if (action != null) b.Click += delegate { action(); };
            return b;
        }
        public void StyleButton(Button b, Color back)
        {
            b.FlatStyle = FlatStyle.Flat; b.BackColor = back;
            b.ForeColor = ReadableOn(back) == Color.Black ? Color.Black : (ReadableOn(Text) == Color.Black ? Text : Color.White);
            b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = Mix(back, Accent, 0.2); b.FlatAppearance.MouseDownBackColor = Mix(back, Accent, 0.4);
            b.Cursor = Cursors.Hand;
        }
        public Label MakeLabel(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, BackColor = Color.Transparent, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        }
        public void Dispose()
        {
            foreach (Font f in new Font[] { Base, Bold, Small, Big, Huge, Mono, MonoBig, MonoMid }) if (f != null) f.Dispose();
        }
    }

    // Owner-drawn tab headers so tabs follow the theme.
    internal sealed class ThemedTabs : TabControl
    {
        private readonly UiTheme _t;
        public ThemedTabs(UiTheme theme)
        {
            _t = theme; SizeMode = TabSizeMode.Fixed; Font = theme.Bold;
            ItemSize = new Size(theme.S(128), theme.S(28)); Padding = new Point(theme.S(8), theme.S(4));
            // Paint the whole control (tab strip background and page border) so no white system chrome shows.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(_t.Back)) g.FillRectangle(back, ClientRectangle);
            Rectangle d = DisplayRectangle;
            using (var page = new SolidBrush(_t.Panel)) g.FillRectangle(page, Rectangle.Inflate(d, 3, 3));
            using (var pen = new Pen(_t.Border)) g.DrawRectangle(pen, d.X - 3, d.Y - 3, d.Width + 5, d.Height + 5);
            for (int i = 0; i < TabCount; i++)
            {
                Rectangle r = GetTabRect(i); bool sel = i == SelectedIndex;
                using (var b = new SolidBrush(sel ? _t.Raised : _t.Panel)) g.FillRectangle(b, r);
                using (var pen = new Pen(_t.Border)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                if (sel) using (var bar = new SolidBrush(_t.Accent)) g.FillRectangle(bar, r.X, r.Bottom - _t.S(3), r.Width, _t.S(3));
                TextRenderer.DrawText(g, TabPages[i].Text, _t.Bold, r, sel ? _t.Accent : _t.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Simple horizontal level meter (0..1) drawn with theme colours.
    internal sealed class LevelMeter : Control
    {
        private readonly UiTheme _t; private double _value, _peak; public Color Bar;
        public LevelMeter(UiTheme theme, Color bar) { _t = theme; Bar = bar; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        public void SetLevel(double v)
        {
            v = Math.Max(0, Math.Min(1, v)); _peak = Math.Max(v, _peak - 0.02);
            if (Math.Abs(v - _value) > 0.004 || _peak > 0) { _value = v; Invalidate(); }
        }
        // 0..32767 linear sample peak mapped onto a -60..0 dBFS scale.
        public void SetPeak(int peak) { SetLevel(peak <= 0 ? 0 : Math.Max(0, (20 * Math.Log10(peak / 32768.0) + 60) / 60.0)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var back = new SolidBrush(_t.Back)) e.Graphics.FillRectangle(back, ClientRectangle);
            int w = (int)(Width * _value);
            Color c = _value > 0.95 ? _t.Bad : _value > 0.8 ? _t.Warn : Bar;
            using (var fill = new SolidBrush(c)) e.Graphics.FillRectangle(fill, 0, 0, w, Height);
            int p = (int)(Width * _peak); using (var pen = new Pen(_t.Text)) e.Graphics.DrawLine(pen, p, 0, p, Height);
            using (var border = new Pen(_t.Border)) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
    }
}
