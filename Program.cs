using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
namespace RadioDispatchConsole
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // A UI-thread exception must never leave a transmitter keyed: report it and keep the console alive.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            // A crash on a background thread (audio, serial) ends the process; log it first. TRC tones stop with the
            // process, so the radio unkeys.
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "error-log.txt"), DateTime.Now.ToString("s") + "  (background thread, console closed)" + Environment.NewLine + e.ExceptionObject + Environment.NewLine + Environment.NewLine); } catch { }
            };
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "error-log.txt"), DateTime.Now.ToString("s") + Environment.NewLine + e.Exception + Environment.NewLine + Environment.NewLine); } catch { }
                foreach (Form f in Application.OpenForms) { ConsoleWindow w = f as ConsoleWindow; if (w != null) w.EmergencyStop("Internal error: " + e.Exception.Message); }
                MessageBox.Show("An internal error occurred and all transmit activity was stopped:\n\n" + e.Exception.Message + "\n\nDetails were added to error-log.txt.", Brand.Product);
            };
            SplashForm splash = null;
            try
            {
                try { splash = new SplashForm(); splash.Show(); splash.Refresh(); } catch (Exception) { splash = null; }
                var window = new ConsoleWindow();
                if (splash != null)
                {
                    if (!window.SplashWanted) { splash.Close(); splash = null; }
                    else { SplashForm s = splash; var hold = new Timer { Interval = 900 }; hold.Tick += delegate { hold.Stop(); hold.Dispose(); s.Close(); }; window.Shown += delegate { hold.Start(); }; }
                }
                Application.Run(window);
            }
            catch (Exception ex)
            {
                try { if (splash != null && !splash.IsDisposed) splash.Close(); } catch { }
                string detail = "";
                try { string log = Path.Combine(AppPaths.DataDir, "startup-error.txt"); File.WriteAllText(log, DateTime.Now.ToString("s") + Environment.NewLine + ex.ToString()); detail = "\n\nDetails saved to: " + log; } catch { }
                MessageBox.Show("The console could not start: " + ex.Message + detail, Brand.Product + " " + ConsoleWindow.VersionText);
            }
        }
    }
    internal static class DispatchWindow
    {
        public static Label MakeLabel(string text, float size, Color color, bool bold)
        { return new Label { Text = text, ForeColor = color, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft }; }
    }
    internal sealed class LabelDialog : Form
    {
        private readonly TextBox _input;
        public string ResultText { get { return _input.Text; } }
        public LabelDialog(string title, string original)
        {
            Text = "Edit local label: " + title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 134);
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(28, 44, 62);
            ForeColor = Color.White;
            var explain = DispatchWindow.MakeLabel(title == "NEW RADIO RESOURCE" ? "Display name for this radio resource." : "Display-only label. Does not program the radio.", 10F, Color.White, false);
            explain.SetBounds(12, 10, 400, 26);
            Controls.Add(explain);
            _input = new TextBox { Text = original, MaxLength = 60, Font = new Font("Segoe UI", 12F) };
            _input.SetBounds(12, 44, 406, 29);
            Controls.Add(_input);
            var ok = new Button { Text = "SAVE", DialogResult = DialogResult.OK };
            ok.SetBounds(235, 88, 85, 31);
            Controls.Add(ok);
            var cancel = new Button { Text = "CANCEL", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(330, 88, 89, 31);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
