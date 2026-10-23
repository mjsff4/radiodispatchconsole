using System;
using System.Drawing;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    // A channel key that always shows: position + full name (wrapping to two lines, then "..."), and underneath in
    // small text the radio's 8-character label and flags (SCAN P1, RX ONLY, HOME). A plain Button clips the second
    // line as soon as a long name wraps.
    internal sealed class ChannelKeyButton : Button
    {
        private readonly UiTheme _t;
        public string Title = "", Sub = "";
        public ChannelKeyButton(UiTheme theme) { _t = theme; UseMnemonic = false; Text = ""; }
        public static int PreferredHeight(UiTheme t) { return t.Bold.Height * 2 + t.Small.Height + t.S(14); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);   // flat background and border from the theme (Text is empty)
            int pad = _t.S(6), subH = Sub.Length > 0 ? _t.Small.Height + _t.S(2) : 0;
            var titleBox = new Rectangle(pad, pad, Width - 2 * pad, Height - 2 * pad - subH);
            TextRenderer.DrawText(e.Graphics, Title, _t.Bold, titleBox, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (subH > 0)
            {
                var subBox = new Rectangle(pad, Height - pad - subH, Width - 2 * pad, subH);
                TextRenderer.DrawText(e.Graphics, Sub, _t.Small, subBox, UiTheme.Mix(ForeColor, BackColor, 0.25),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }
    }

    // One radio resource tile, MCC-style: status strip, name, channel, state badges and RX/TX meter.
    internal sealed class RadioCard : Panel
    {
        private readonly UiTheme _t;
        public RadioProfile Radio;
        public RadioState State;
        public bool IsSelected, Transmitting, Receiving, Muted, Flash, Live;
        public string StatusText = "", ChannelText = "", ZoneText = "", LinkText = "";
        public double Level;
        public readonly CheckBox Group;
        public readonly Button Mute;
        public Action Selected;
        public RadioCard(UiTheme theme, RadioProfile radio, RadioState state, int height)
        {
            _t = theme; Radio = radio; State = state; Height = height; Margin = new Padding(theme.S(3)); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            var strip = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = theme.ButtonH + theme.S(6), BackColor = Color.Transparent, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0) };
            Mute = theme.MakeButton("MUTE", null, 70); Mute.Height = Math.Max(theme.S(24), theme.ButtonH - theme.S(6));
            Group = new CheckBox { Text = "GROUP", ForeColor = theme.Text, BackColor = Color.Transparent, Font = theme.Small, AutoSize = true, Margin = new Padding(theme.S(4), theme.S(6), theme.S(4), 0), Checked = state.Group };
            strip.Controls.Add(Mute); strip.Controls.Add(Group);
            Controls.Add(strip);
            strip.Click += delegate { if (Selected != null) Selected(); };
        }
        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Selected != null) Selected(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; int s4 = _t.S(4), stripW = _t.S(7);
            Color back = IsSelected ? _t.Selected : _t.Panel;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);
            Color status = State.Emergency ? (Flash ? _t.Bad : _t.Warn) : Transmitting ? _t.Bad : Receiving ? _t.Good : IsSelected ? _t.Accent : _t.Border;
            using (var b = new SolidBrush(status)) g.FillRectangle(b, 0, 0, stripW, Height);
            if (IsSelected) using (var p = new Pen(_t.Accent, _t.S(2))) g.DrawRectangle(p, _t.S(1), _t.S(1), Width - _t.S(3), Height - _t.S(3));
            int x = stripW + _t.S(8), y = _t.S(6), right = Width - _t.S(8);
            // badges, right aligned
            int bx = right;
            bx = Badge(g, bx, y, "EMERG", State.Emergency, Flash ? _t.Bad : _t.Warn);
            bx = Badge(g, bx, y, "TX", Transmitting, _t.Bad);
            bx = Badge(g, bx, y, "RX", Receiving, _t.Good);
            if (Muted) bx = Badge(g, bx, y, "MUTED", true, _t.Dim);
            TextRenderer.DrawText(g, Radio.Name, _t.Big, new Rectangle(x, y, Math.Max(10, bx - x - s4), _t.S(24)), _t.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            y += _t.S(26);
            int stripTop = Height - (_t.ButtonH + _t.S(6));
            if (y + _t.S(20) <= stripTop)
            {
                TextRenderer.DrawText(g, ChannelText, _t.Bold, new Rectangle(x, y, right - x, _t.S(20)), _t.Accent, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                y += _t.S(20);
            }
            if (y + _t.S(16) <= stripTop)
            {
                TextRenderer.DrawText(g, ZoneText + (StatusText.Length > 0 ? "   " + StatusText : ""), _t.Small, new Rectangle(x, y, right - x, _t.S(16)), _t.Dim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                y += _t.S(17);
            }
            if (y + _t.S(14) <= stripTop - _t.S(7) && LinkText.Length > 0)
            {
                TextRenderer.DrawText(g, LinkText, _t.Small, new Rectangle(x, y, right - x, _t.S(14)), Live ? _t.Good : _t.Dim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                y += _t.S(15);
            }
            // level bar along the bottom of the text area
            int barY = stripTop - _t.S(4);
            using (var b = new SolidBrush(_t.Back)) g.FillRectangle(b, x, barY, right - x, _t.S(3));
            using (var b = new SolidBrush(Transmitting ? _t.Bad : _t.Good)) g.FillRectangle(b, x, barY, (int)((right - x) * Math.Max(0, Math.Min(1, Level))), _t.S(3));
        }
        private int Badge(Graphics g, int rightEdge, int y, string text, bool on, Color color)
        {
            if (!on) return rightEdge;
            Size size = TextRenderer.MeasureText(text, _t.Small); int w = size.Width + _t.S(8), h = _t.S(18);
            var r = new Rectangle(rightEdge - w, y + _t.S(2), w, h);
            using (var b = new SolidBrush(color)) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, text, _t.Small, r, UiTheme.ReadableOn(color), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return r.X - _t.S(4);
        }
    }

    // The selected-radio display ("LCD").
    internal sealed class LcdPanel : Control
    {
        private readonly UiTheme _t;
        public string TopLeft = "", TopRight = "", Main = "", Detail = "", RightTop = "", RightMain = "", Mirror = "";
        public Color RightColor;
        public bool Alarm, Flash;
        public LcdPanel(UiTheme theme)
        {
            _t = theme; RightColor = theme.Good;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        public bool Compact;   // shorter display for small screens (Compact layout)
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; int pad = _t.S(Compact ? 8 : 12);
            Color back = Alarm && Flash ? UiTheme.Mix(_t.Lcd, _t.Bad, 0.45) : _t.Lcd;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(_t.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            int rightW = Math.Max(_t.S(Compact ? 150 : 180), Width / 3);
            int top = _t.S(Compact ? 4 : 6);
            var left = new Rectangle(pad, top, Width - rightW - pad * 2, Height - top * 2);
            var right = new Rectangle(Width - rightW - pad, top, rightW, Height - top * 2);
            // Row positions: title, channel name, detail, radio display mirror.
            int yMain = _t.S(Compact ? 18 : 20), hMain = _t.S(Compact ? 32 : 48), yDetail = _t.S(Compact ? 50 : 70), yMirror = _t.S(Compact ? 68 : 90);
            TextRenderer.DrawText(g, TopLeft, _t.Bold, new Rectangle(left.X, left.Y, left.Width, _t.S(18)), _t.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Main, Compact ? _t.MonoMid : _t.MonoBig, new Rectangle(left.X, left.Y + yMain, left.Width, hMain), _t.LcdText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Detail, _t.Base, new Rectangle(left.X, left.Y + yDetail, left.Width, _t.S(20)), _t.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (Mirror.Length > 0)
                TextRenderer.DrawText(g, Mirror, _t.Mono, new Rectangle(left.X, left.Y + yMirror, left.Width + rightW, _t.S(18)), _t.Warn, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, TopRight, _t.Small, new Rectangle(right.X, right.Y, right.Width, _t.S(18)), _t.Warn, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, RightTop, _t.Bold, new Rectangle(right.X, right.Y + _t.S(Compact ? 18 : 24), right.Width, _t.S(20)), RightColor, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, RightMain, _t.Big, new Rectangle(right.X, right.Y + _t.S(Compact ? 36 : 44), right.Width, _t.S(28)), RightColor, TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
        }
    }
}
