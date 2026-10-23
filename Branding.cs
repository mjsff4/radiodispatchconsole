using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Dispatch Console")]
[assembly: AssemblyDescription("Radio dispatch console for the Motorola XTL5000 consolette (tone remote + SB9600)")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Dispatch Console")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Mike Steege. Licensed under the GNU GPL v3.")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]

// Department branding: names, logo, icon and settings folder, read from branding\brand.ini beside the program (or one
// folder up), so each department can brand the console without changing code. Anything missing uses the defaults below.
namespace RadioDispatchConsole
{
    internal static class Brand
    {
        private static string _department = "DISPATCH CENTER", _short = "DISPATCH", _product = "Dispatch Console", _motto = "", _established = "";
        private static string _logoFile = "logo.png", _iconFile = "app.ico", _dataFolder = "Dispatch Console";
        private static Image _logo; private static Icon _icon; private static bool _loaded, _iniLoaded;

        public static string Department { get { LoadIni(); return _department; } }
        public static string Short { get { LoadIni(); return _short; } }
        public static string Product { get { LoadIni(); return _product; } }
        public static string Motto { get { LoadIni(); return _motto; } }
        public static string Established { get { LoadIni(); return _established; } }
        // Folder name under C:\ProgramData that holds settings and recordings.
        public static string DataFolder { get { LoadIni(); return _dataFolder; } }
        public static Image Logo { get { Load(); return _logo; } }
        public static Icon AppIcon { get { Load(); return _icon; } }

        private static string[] BrandingFolders()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return new string[] { Path.Combine(dir, "branding"), Path.Combine(Path.Combine(dir, ".."), "branding"), dir };
        }

        private static void LoadIni()
        {
            if (_iniLoaded) return; _iniLoaded = true;
            try
            {
                foreach (string folder in BrandingFolders())
                {
                    string file = Path.Combine(folder, "brand.ini");
                    if (!File.Exists(file)) continue;
                    foreach (string raw in File.ReadAllLines(file))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                        int eq = line.IndexOf('='); if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant(), value = line.Substring(eq + 1).Trim();
                        if (key == "department" && value.Length > 0) _department = value;
                        else if (key == "shortname" && value.Length > 0) _short = value;
                        else if (key == "product" && value.Length > 0) _product = value;
                        else if (key == "motto") _motto = value;
                        else if (key == "established") _established = value;
                        else if (key == "logo" && value.Length > 0) _logoFile = value;
                        else if (key == "icon" && value.Length > 0) _iconFile = value;
                        else if (key == "datafolder" && value.Length > 0 && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) _dataFolder = value;
                    }
                    return;
                }
            }
            catch (Exception) { }
        }

        // Files in the branding folder win, so a department can swap the logo without rebuilding; the copies embedded at
        // build time (/resource:branding\logo.png) are the fallback.
        private static void Load()
        {
            if (_loaded) return; _loaded = true; LoadIni();
            try
            {
                foreach (string folder in BrandingFolders())
                {
                    string logo = Path.Combine(folder, _logoFile), icon = Path.Combine(folder, _iconFile);
                    if (_logo == null && File.Exists(logo)) using (var fs = File.OpenRead(logo)) _logo = CopyImage(fs);
                    if (_icon == null && File.Exists(icon)) _icon = new Icon(icon);
                }
            }
            catch (Exception) { }
            try
            {
                Assembly asm = typeof(Brand).Assembly;
                if (_logo == null) using (Stream s = asm.GetManifestResourceStream("RadioDispatchConsole.logo.png")) if (s != null) _logo = CopyImage(s);
                if (_icon == null) using (Stream s = asm.GetManifestResourceStream("RadioDispatchConsole.app.ico")) if (s != null) _icon = new Icon(s);
                if (_icon == null) _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception) { }
        }
        // Image.FromStream needs its stream for the image's lifetime; copy into an independent bitmap.
        private static Image CopyImage(Stream s)
        {
            using (var ms = new MemoryStream()) { s.CopyTo(ms); ms.Position = 0; using (Image img = Image.FromStream(ms)) return new Bitmap(img); }
        }
        public static void Apply(Form form) { if (AppIcon != null) form.Icon = AppIcon; }
        // "Motto  -  Est. 2025" for the About box, leaving out whichever is blank.
        public static string MottoLine()
        {
            if (Motto.Length > 0 && Established.Length > 0) return Motto + "   \u00B7   " + Established;
            return Motto + Established;
        }
    }

    internal sealed class SplashForm : Form
    {
        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; TopMost = true;
            Size = new Size(520, 600); BackColor = Color.FromArgb(16, 14, 13); Brand.Apply(this);
            var logo = new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(60, 30, 400, 400), BackColor = Color.Transparent };
            var name = new Label { Text = Brand.Department, ForeColor = Color.FromArgb(212, 165, 55), Font = new Font("Segoe UI", 20F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 440, 520, 44) };
            var sub = new Label { Text = "DISPATCH CONSOLE  " + ConsoleWindow.VersionText, ForeColor = Color.FromArgb(242, 232, 205), Font = new Font("Segoe UI", 11F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 484, 520, 26) };
            var motto = new Label { Text = Brand.Motto, ForeColor = Color.FromArgb(190, 35, 42), Font = new Font("Segoe UI", 9F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 516, 520, 22) };
            var status = new Label { Text = "Loading settings...", ForeColor = Color.FromArgb(165, 150, 120), Font = new Font("Segoe UI", 8.5F), TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 556, 520, 22) };
            Controls.Add(logo); Controls.Add(name); Controls.Add(sub); Controls.Add(motto); Controls.Add(status);
            Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(Color.FromArgb(212, 165, 55), 3)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); };
        }
    }

    internal sealed class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "About " + Brand.Product; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 600); BackColor = Color.FromArgb(16, 14, 13); Brand.Apply(this);
            var logo = new PictureBox { Image = Brand.Logo, SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(130, 16, 300, 300), BackColor = Color.Transparent };
            var name = new Label { Text = Brand.Department, ForeColor = Color.FromArgb(212, 165, 55), Font = new Font("Segoe UI", 18F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(0, 324, 560, 38) };
            var body = new Label
            {
                ForeColor = Color.FromArgb(242, 232, 205), Font = new Font("Segoe UI", 9.5F), TextAlign = ContentAlignment.TopCenter, Bounds = new Rectangle(20, 366, 520, 180),
                Text = Brand.Product + " " + ConsoleWindow.VersionText + "\r\n" + Brand.MottoLine() + "\r\n\r\n" +
                       "Motorola XTL5000 consolette control over tone remote (PLN1687 TRC) and SB9600 (RIB).\r\n" +
                       "Live tone-remote TX, SB9600 status/display/head keys, paging, instant recall and logging.\r\n\r\n" +
                       "Copyright (c) 2026 Mike Steege. Free software under the GNU GPL v3. Not Motorola MCC 5500/7500 software; not affiliated with Motorola Solutions.\r\n" +
                       "Protocol references: XTL5000 consolette manual 6881098C21-P, RadioConsole2 (W3AXL),\r\nXTL-SB9600-Playground (W3AXL / Paul Banks). See SOURCES.md."
            };
            var ok = new Button { Text = "OK", Bounds = new Rectangle(230, 552, 100, 32), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(242, 232, 205), BackColor = Color.FromArgb(120, 26, 30) };
            Controls.Add(logo); Controls.Add(name); Controls.Add(body); Controls.Add(ok); AcceptButton = ok; CancelButton = ok;
        }
    }
}
