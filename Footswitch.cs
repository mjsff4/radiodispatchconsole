using System;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace RadioDispatchConsole
{
    // Footswitch PTT that works whichever window has focus (CAD, softphone ...):
    //  - a USB footswitch that shows up as a game controller (read through winmm, no driver or library needed), or
    //  - a plain switch on a COM port control line: wire it between DTR (DB9 pin 4) and DSR (pin 6), or RTS (pin 7)
    //    and CTS (pin 8). The console holds DTR and RTS high, so closing the switch raises the input line.
    //  - a USB pedal that types a key: that key is caught system-wide (a low-level keyboard hook) and kept from other
    //    programs, so it keys PTT even when a CAD or softphone has focus. Program the pedal to a key nothing else
    //    uses (F13-F24, Scroll Lock, Pause).
    // Any read error counts as "released", so an unplugged switch can never hold the transmitter keyed. A keyboard
    // pedal cannot report being unplugged mid-press; the PTT timeout covers that case.
    internal sealed class Footswitch : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct JoyInfoEx
        {
            public int Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
        }
        [DllImport("winmm.dll")] private static extern int joyGetPosEx(int id, ref JoyInfoEx info);
        private const int JoyReturnButtons = 0x80;

        // Low-level keyboard hook (keyboard-style pedals) on its own thread with its own message loop. Windows drops a
        // low-level hook whose thread is slow to answer, so it never runs on the console's busy UI thread.
        private delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct KeyInfo { public int Vk, Scan, Flags, Time; public IntPtr Extra; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hook, KeyboardProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
        [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private System.Threading.Thread _hookThread;
        private uint _hookThreadId;
        private string _hookError = "";
        private KeyboardProc _proc;   // kept referenced so the garbage collector never frees the callback
        private volatile IntPtr _hook = IntPtr.Zero;
        private int _vk;
        private volatile bool _keyDown;
        public int KeyEvents;   // how many pedal key presses / releases were seen (shown while setting it up)

        private readonly FootswitchSettings _cfg;
        private SerialPort _port;
        public string Problem = "";
        public bool Ready { get { return Problem.Length == 0; } }

        public Footswitch(FootswitchSettings cfg) { _cfg = cfg; }

        // A short text that changes whenever the settings that matter change (the console reopens the switch then).
        public static string Signature(FootswitchSettings c)
        {
            return c == null ? "" : c.Source + "|" + c.ControllerNumber + "|" + c.Button + "|" + (c.PortName ?? "") + "|" + c.Line + "|" + c.Invert + "|" + (c.Key ?? "");
        }

        public void Open()
        {
            Close(); Problem = "";
            if (_cfg.Source == FootswitchSource.KeyboardKey) { OpenHook(); return; }
            if (_cfg.Source != FootswitchSource.ComPortLine) return;
            if (String.IsNullOrEmpty(_cfg.PortName)) { Problem = "no COM port chosen for the footswitch"; return; }
            try
            {
                _port = new SerialPort(_cfg.PortName, 9600, Parity.None, 8, StopBits.One);
                _port.Handshake = Handshake.None; _port.Open();
                _port.DtrEnable = true; _port.RtsEnable = true;   // supply for the switch
            }
            catch (Exception ex) { Problem = "could not open " + _cfg.PortName + ": " + ex.Message; Close(); }
        }

        // Returns false when the switch cannot be read (unplugged, port gone); pressed is then false.
        public bool Read(out bool pressed)
        {
            pressed = false;
            try
            {
                bool raw;
                if (_cfg.Source == FootswitchSource.GameController)
                {
                    var info = new JoyInfoEx { Size = Marshal.SizeOf(typeof(JoyInfoEx)), Flags = JoyReturnButtons };
                    int result = joyGetPosEx(Math.Max(0, _cfg.ControllerNumber - 1), ref info);
                    if (result != 0) { Problem = "game controller " + _cfg.ControllerNumber + " not found (code " + result + ")"; return false; }
                    raw = (info.Buttons & (1 << (Math.Max(1, Math.Min(32, _cfg.Button)) - 1))) != 0;
                }
                else if (_cfg.Source == FootswitchSource.ComPortLine)
                {
                    if (_port == null || !_port.IsOpen) { if (Problem.Length == 0) Problem = "COM port " + _cfg.PortName + " is closed"; return false; }
                    switch (_cfg.Line)
                    {
                        case SerialLine.DSR: raw = _port.DsrHolding; break;
                        case SerialLine.DCD: raw = _port.CDHolding; break;
                        default: raw = _port.CtsHolding; break;
                    }
                }
                else if (_cfg.Source == FootswitchSource.KeyboardKey)
                {
                    if (_hook == IntPtr.Zero) { if (Problem.Length == 0) Problem = "keyboard pedal hook is not running"; return false; }
                    raw = _keyDown;
                }
                else return false;
                Problem = ""; pressed = _cfg.Invert ? !raw : raw; return true;
            }
            catch (Exception ex) { Problem = ex.Message; Close(); return false; }
        }

        private void OpenHook()
        {
            System.Windows.Forms.Keys key = Hotkey.Parse(_cfg.Key);
            if (key == System.Windows.Forms.Keys.None || (key & System.Windows.Forms.Keys.Modifiers) != 0) { Problem = "pedal key '" + _cfg.Key + "' is not a single key (use F13, Scroll, Pause ...)"; return; }
            _vk = (int)(key & System.Windows.Forms.Keys.KeyCode); _keyDown = false; _hookError = "";
            var ready = new System.Threading.ManualResetEvent(false);
            _proc = OnKey;
            _hookThread = new System.Threading.Thread(delegate()
            {
                try
                {
                    _hookThreadId = GetCurrentThreadId();
                    using (var me = System.Diagnostics.Process.GetCurrentProcess())
                        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(me.MainModule.ModuleName), 0);
                    if (_hook == IntPtr.Zero) _hookError = "Windows refused the keyboard hook (error " + Marshal.GetLastWin32Error() + ")";
                }
                catch (Exception ex) { _hookError = ex.Message; }
                ready.Set();
                if (_hook == IntPtr.Zero) return;
                Msg m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                try { UnhookWindowsHookEx(_hook); } catch (Exception) { }
                _hook = IntPtr.Zero; _keyDown = false;
            });
            _hookThread.IsBackground = true; _hookThread.Name = "Footswitch key"; _hookThread.Start();
            ready.WaitOne(2000);   // not closed here: a slow hook thread may still set it
            if (_hook == IntPtr.Zero) Problem = _hookError.Length > 0 ? _hookError : "keyboard pedal hook did not start";
        }
        private IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                var info = (KeyInfo)Marshal.PtrToStructure(data, typeof(KeyInfo));
                if (info.Vk == _vk)
                {
                    int m = message.ToInt32();
                    bool down = m == 0x100 || m == 0x104, up = m == 0x101 || m == 0x105;
                    if (down && !_keyDown) { _keyDown = true; KeyEvents++; }
                    if (up && _keyDown) { _keyDown = false; KeyEvents++; }
                    if (down || up) return (IntPtr)1;   // swallowed: other programs never see the pedal key
                }
            }
            return CallNextHookEx(_hook, code, message, data);
        }
        public void Close()
        {
            if (_hookThread != null)
            {
                try { PostThreadMessage(_hookThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); _hookThread.Join(1000); } catch (Exception) { }
                _hookThread = null;
            }
            _keyDown = false;
            if (_port == null) return;
            try { if (_port.IsOpen) _port.Close(); } catch (Exception) { }
            try { _port.Dispose(); } catch (Exception) { }
            _port = null;
        }
        public void Dispose() { Close(); }
    }
}
