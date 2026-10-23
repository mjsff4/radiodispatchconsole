using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace RadioDispatchConsole
{
    // Parses and prints keyboard shortcuts in one consistent form ("Ctrl+1", "Shift+F12", "PageUp", "Escape").
    // Every button caption, tooltip, setting and the keyboard dispatcher go through this class, so the text printed on
    // a button is always the key that actually triggers it. (KeysConverter alone cannot read "Ctrl+1": it needs "D1".)
    internal static class Hotkey
    {
        private static readonly Dictionary<string, Keys> Aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            { "esc", Keys.Escape }, { "escape", Keys.Escape }, { "del", Keys.Delete }, { "delete", Keys.Delete }, { "ins", Keys.Insert }, { "insert", Keys.Insert },
            { "pgup", Keys.PageUp }, { "pageup", Keys.PageUp }, { "prior", Keys.PageUp }, { "pgdn", Keys.PageDown }, { "pagedown", Keys.PageDown }, { "next", Keys.PageDown },
            { "space", Keys.Space }, { "spacebar", Keys.Space }, { "enter", Keys.Return }, { "return", Keys.Return }, { "bksp", Keys.Back }, { "backspace", Keys.Back },
            { "up", Keys.Up }, { "down", Keys.Down }, { "left", Keys.Left }, { "right", Keys.Right }, { "home", Keys.Home }, { "end", Keys.End }, { "tab", Keys.Tab },
            { "pause", Keys.Pause }, { "break", Keys.Pause }, { "minus", Keys.OemMinus }, { "-", Keys.OemMinus }, { "plus", Keys.Oemplus }, { "=", Keys.Oemplus },
            { "comma", Keys.Oemcomma }, { ",", Keys.Oemcomma }, { "period", Keys.OemPeriod }, { ".", Keys.OemPeriod }, { "capslock", Keys.CapsLock }, { "scroll", Keys.Scroll }, { "scrolllock", Keys.Scroll }
        };

        public static Keys Parse(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return Keys.None;
            string t = text.Trim();
            if (t.EndsWith("++")) t = t.Substring(0, t.Length - 2) + "+plus";   // "Ctrl++"
            string[] parts = t.Split('+');
            Keys mods = Keys.None, code = Keys.None;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim(); if (p.Length == 0) return Keys.None;
                if (i < parts.Length - 1)
                {
                    string m = p.ToLowerInvariant();
                    if (m == "ctrl" || m == "control" || m == "ctl") mods |= Keys.Control;
                    else if (m == "shift") mods |= Keys.Shift;
                    else if (m == "alt") mods |= Keys.Alt;
                    else return Keys.None;
                    continue;
                }
                code = KeyName(p);
            }
            return code == Keys.None ? Keys.None : code | mods;
        }

        private static Keys KeyName(string p)
        {
            Keys k;
            if (p.Length == 1 && p[0] >= '0' && p[0] <= '9') return Keys.D0 + (p[0] - '0');
            if (p.Length == 1 && Char.IsLetter(p[0])) { char c = Char.ToUpperInvariant(p[0]); if (c >= 'A' && c <= 'Z') return Keys.A + (c - 'A'); return Keys.None; }
            if (Aliases.TryGetValue(p, out k)) return k;
            string lower = p.ToLowerInvariant();
            foreach (string prefix in new string[] { "numpad", "num" })
                if (lower.StartsWith(prefix) && lower.Length == prefix.Length + 1 && Char.IsDigit(lower[prefix.Length])) return Keys.NumPad0 + (lower[prefix.Length] - '0');
            if (lower.StartsWith("f") && lower.Length <= 3) { int n; if (Int32.TryParse(lower.Substring(1), out n) && n >= 1 && n <= 24) return Keys.F1 + (n - 1); return Keys.None; }
            // Anything else must be a real key name (D1, Oemtilde, Multiply ...). Numbers are not key names.
            if (Char.IsDigit(p[0])) return Keys.None;
            try { k = (Keys)Enum.Parse(typeof(Keys), p, true); } catch (Exception) { return Keys.None; }
            if ((k & Keys.Modifiers) != Keys.None || (k & Keys.KeyCode) != k) return Keys.None;
            if (k == Keys.None || k == Keys.ShiftKey || k == Keys.ControlKey || k == Keys.Menu || k == Keys.LButton || k == Keys.RButton || k == Keys.MButton
                || k == Keys.XButton1 || k == Keys.XButton2 || k == Keys.LShiftKey || k == Keys.RShiftKey || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LMenu || k == Keys.RMenu) return Keys.None;
            return k;
        }

        public static string Format(Keys keys)
        {
            Keys code = keys & Keys.KeyCode; if (code == Keys.None) return "";
            string s = "";
            if ((keys & Keys.Control) != 0) s += "Ctrl+";
            if ((keys & Keys.Shift) != 0) s += "Shift+";
            if ((keys & Keys.Alt) != 0) s += "Alt+";
            string name;
            if (code >= Keys.D0 && code <= Keys.D9) name = ((int)(code - Keys.D0)).ToString();
            else if (code >= Keys.NumPad0 && code <= Keys.NumPad9) name = "NumPad" + (int)(code - Keys.NumPad0);
            else if (code == Keys.PageUp) name = "PageUp";
            else if (code == Keys.PageDown) name = "PageDown";
            else if (code == Keys.Return) name = "Enter";
            else if (code == Keys.Escape) name = "Escape";
            else if (code == Keys.Back) name = "Backspace";
            else if (code == Keys.CapsLock) name = "CapsLock";
            else if (code == Keys.OemMinus) name = "Minus";
            else if (code == Keys.Oemplus) name = "Plus";
            else if (code == Keys.Oemcomma) name = "Comma";
            else if (code == Keys.OemPeriod) name = "Period";
            else name = code.ToString();
            return s + name;
        }

        // Normalised text for captions: "ctrl+1" -> "Ctrl+1". Unknown text is returned with a marker so it is visible.
        public static string Display(string text)
        {
            Keys k = Parse(text);
            if (k != Keys.None) return Format(k);
            return String.IsNullOrWhiteSpace(text) ? "" : text.Trim() + " (?)";
        }

        // Combinations Windows keeps for itself (or that would trap the operator) and that a console must not rely on.
        public static bool Reserved(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            if (k == (Keys.Alt | Keys.F4) || k == (Keys.Alt | Keys.Tab) || k == (Keys.Control | Keys.Escape) || k == (Keys.Alt | Keys.Escape)) return true;
            if (k == (Keys.Control | Keys.Alt | Keys.Delete) || code == Keys.LWin || code == Keys.RWin || code == Keys.PrintScreen || code == Keys.Apps) return true;
            return k == Keys.Tab || k == (Keys.Shift | Keys.Tab);
        }
    }
}
