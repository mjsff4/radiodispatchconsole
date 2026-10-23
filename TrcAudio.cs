using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Live transmit audio for a Motorola Tone Remote Control (TRC) consolette (PLN1687 board).
//
// What the XTL5000 consolette manual (6881098C21-P, section 3.4 / Figure 3-4) specifies, and what this engine does:
//   * Every function is preceded by a high-level guard tone (HLGT, usually 2175 Hz), 120 ms.
//   * A function tone (FT) follows for 40 ms. For a line push-to-talk the FT is the tone assigned to a mode.
//   * After a PTT function tone the console sends a continuous low-level guard tone (LLGT), 30 dB below the HLGT,
//     together with the voice. The consolette dekeys when the LLGT is no longer detected.
//   * The TRC board notches 2175 Hz out of the transmit audio, so microphone audio is notched here too.
// Voice is limited so its peaks never exceed the HLGT peak (+ a configurable offset): the board's AGC takes its
// reference from the HLGT.
namespace RadioDispatchConsole
{
    internal static class WinMm
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct Format { public ushort tag, channels; public uint rate, bytes; public ushort align, bits, extra; }
        // WAVEFORMATEXTENSIBLE (40 bytes) for 6/8-channel output.
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct FormatExt { public ushort tag, channels; public uint rate, bytes; public ushort align, bits, extra, validBits; public uint channelMask; public Guid subFormat; }
        public static readonly Guid PcmSubFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");
        public static FormatExt Multi(int rate, int channels, uint mask)
        {
            return new FormatExt { tag = 0xFFFE, channels = (ushort)channels, rate = (uint)rate, bytes = (uint)(rate * channels * 2), align = (ushort)(channels * 2), bits = 16, extra = 22, validBits = 16, channelMask = mask, subFormat = PcmSubFormat };
        }
        // Channel order for WAVEFORMATEXTENSIBLE: FL FR FC LFE BL BR SL SR. 7.1 mask 0x63F, 5.1 (back) mask 0x3F.
        public static void JackLayout(TxJack jack, bool fiveOne, out int channels, out uint mask, out int slotA, out int slotB)
        {
            channels = fiveOne ? 6 : 8; mask = fiveOne ? 0x3Fu : 0x63Fu;
            if (jack == TxJack.Side71) { slotA = 6; slotB = 7; }
            else if (jack == TxJack.Rear71) { slotA = 4; slotB = 5; }
            else if (jack == TxJack.CenterBass71) { slotA = 2; slotB = 3; }
            else { channels = 1; mask = 0; slotA = 0; slotB = 0; }
        }
        // Copies mono samples into two slots of an interleaved multichannel frame; every other channel is silent.
        public static void Interleave(short[] mono, int frames, int channels, int slotA, int slotB, short[] dst)
        {
            Array.Clear(dst, 0, frames * channels);
            for (int i = 0; i < frames; i++) { dst[i * channels + slotA] = mono[i]; dst[i * channels + slotB] = mono[i]; }
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct Header { public IntPtr data; public uint length, recorded; public UIntPtr user; public uint flags, loops; public IntPtr next; public UIntPtr reserved; }
        public static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(Header));
        public const uint Done = 1;
        public static Format Mono(int rate) { return new Format { tag = 1, channels = 1, rate = (uint)rate, bytes = (uint)rate * 2, align = 2, bits = 16 }; }
        public static string Err(uint code, string action)
        {
            string hint = code == 2 ? "invalid device number" : code == 4 ? "device already in use by another program" : code == 32 ? "device does not accept 48 kHz mono 16-bit audio" : code == 6 ? "no driver" : "see Windows sound settings";
            return action + " failed (WinMM code " + code + ": " + hint + ").";
        }
        [DllImport("winmm.dll")] public static extern uint waveOutOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll", EntryPoint = "waveOutOpen")] public static extern uint waveOutOpenExt(out IntPtr handle, uint id, ref FormatExt format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveOutClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveInOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] public static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint waveInClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    }

    internal sealed class PcmBlock
    {
        public IntPtr Header, Data; public bool Prepared, Queued; public int Samples;
        public PcmBlock(int samples)
        {
            Samples = samples; Data = Marshal.AllocHGlobal(samples * 2); Header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinMm.Header)));
            Marshal.StructureToPtr(new WinMm.Header { data = Data, length = (uint)(samples * 2) }, Header, false);
        }
        public bool IsDone { get { return ((WinMm.Header)Marshal.PtrToStructure(Header, typeof(WinMm.Header))).flags % 2 == 1; } }
        public uint Recorded { get { return ((WinMm.Header)Marshal.PtrToStructure(Header, typeof(WinMm.Header))).recorded; } }
        public void ClearRecorded() { Marshal.WriteInt32(Header, (int)Marshal.OffsetOf(typeof(WinMm.Header), "recorded"), 0); }
        public void Free() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
    }

    // Mono 48 kHz PCM16 playback with a ring of small blocks, polled (no callbacks).
    internal sealed class MonoOutStream : IDisposable
    {
        private IntPtr _handle; private readonly List<PcmBlock> _blocks = new List<PcmBlock>(); private int _next;
        public readonly int BlockSamples;
        private int _channels = 1, _slotA, _slotB; private short[] _inter;
        public string Layout = "mono";
        public MonoOutStream(int blockSamples) { BlockSamples = blockSamples; }
        public void Open(int deviceId, int rate, int blocks) { Open(deviceId, rate, blocks, TxJack.WholeDevice); }
        public void Open(int deviceId, int rate, int blocks, TxJack jack)
        {
            uint r;
            if (jack == TxJack.WholeDevice)
            {
                WinMm.Format f = WinMm.Mono(rate);
                r = WinMm.waveOutOpen(out _handle, (uint)deviceId, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            }
            else
            {
                uint mask; WinMm.JackLayout(jack, false, out _channels, out mask, out _slotA, out _slotB);
                WinMm.FormatExt fx = WinMm.Multi(rate, _channels, mask);
                r = WinMm.waveOutOpenExt(out _handle, (uint)deviceId, ref fx, IntPtr.Zero, IntPtr.Zero, 0);
                Layout = "7.1 " + jack;
                if (r != 0 && jack != TxJack.Side71)
                {
                    WinMm.JackLayout(jack, true, out _channels, out mask, out _slotA, out _slotB);
                    fx = WinMm.Multi(rate, _channels, mask);
                    r = WinMm.waveOutOpenExt(out _handle, (uint)deviceId, ref fx, IntPtr.Zero, IntPtr.Zero, 0);
                    Layout = "5.1 " + jack;
                }
                if (r != 0) { _handle = IntPtr.Zero; throw new InvalidOperationException(WinMm.Err(r, "Open " + jack + " jack") + " Set Windows Sound > Speakers > Configure to 7.1 (or 5.1 for rear/center) and turn off enhancements / speaker fill."); }
                _inter = new short[BlockSamples * _channels];
            }
            if (r != 0) { _handle = IntPtr.Zero; throw new InvalidOperationException(WinMm.Err(r, "Open transmit output")); }
            try
            {
                for (int i = 0; i < blocks; i++)
                {
                    var b = new PcmBlock(BlockSamples * _channels); _blocks.Add(b);
                    r = WinMm.waveOutPrepareHeader(_handle, b.Header, WinMm.HeaderSize);
                    if (r != 0) throw new InvalidOperationException(WinMm.Err(r, "Prepare transmit buffer"));
                    b.Prepared = true;
                }
            }
            catch { Dispose(); throw; }
        }
        public bool HasFree { get { PcmBlock b = _blocks[_next]; return !b.Queued || b.IsDone; } }
        public bool Queue(short[] samples)
        {
            PcmBlock b = _blocks[_next]; if (b.Queued && !b.IsDone) return false;
            if (_channels == 1) Marshal.Copy(samples, 0, b.Data, BlockSamples);
            else { WinMm.Interleave(samples, BlockSamples, _channels, _slotA, _slotB, _inter); Marshal.Copy(_inter, 0, b.Data, _inter.Length); }
            uint r = WinMm.waveOutWrite(_handle, b.Header, WinMm.HeaderSize);
            if (r != 0) throw new InvalidOperationException(WinMm.Err(r, "Write transmit audio"));
            b.Queued = true; _next = (_next + 1) % _blocks.Count; return true;
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero) WinMm.waveOutReset(_handle);
            foreach (PcmBlock b in _blocks) { if (b.Prepared && _handle != IntPtr.Zero) WinMm.waveOutUnprepareHeader(_handle, b.Header, WinMm.HeaderSize); b.Free(); }
            _blocks.Clear();
            if (_handle != IntPtr.Zero) { WinMm.waveOutClose(_handle); _handle = IntPtr.Zero; }
        }
    }

    internal sealed class MonoInStream : IDisposable
    {
        private IntPtr _handle; private readonly List<PcmBlock> _blocks = new List<PcmBlock>(); private int _next;
        public void Open(int deviceId, int rate, int blocks, int blockSamples)
        {
            WinMm.Format f = WinMm.Mono(rate);
            uint r = WinMm.waveInOpen(out _handle, (uint)deviceId, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { _handle = IntPtr.Zero; throw new InvalidOperationException(WinMm.Err(r, "Open microphone")); }
            try
            {
                for (int i = 0; i < blocks; i++)
                {
                    var b = new PcmBlock(blockSamples); _blocks.Add(b);
                    r = WinMm.waveInPrepareHeader(_handle, b.Header, WinMm.HeaderSize);
                    if (r != 0) throw new InvalidOperationException(WinMm.Err(r, "Prepare microphone buffer"));
                    b.Prepared = true;
                    r = WinMm.waveInAddBuffer(_handle, b.Header, WinMm.HeaderSize);
                    if (r != 0) throw new InvalidOperationException(WinMm.Err(r, "Queue microphone buffer"));
                    b.Queued = true;
                }
                r = WinMm.waveInStart(_handle);
                if (r != 0) throw new InvalidOperationException(WinMm.Err(r, "Start microphone"));
            }
            catch { Dispose(); throw; }
        }
        // Copies every completed block into dst (appending) and requeues it. Returns samples appended.
        public int ReadAll(ShortFifo dst)
        {
            int total = 0;
            for (int guard = 0; guard < _blocks.Count; guard++)
            {
                PcmBlock b = _blocks[_next]; if (!b.IsDone) break;
                int n = (int)Math.Min(b.Recorded, (uint)(b.Samples * 2)) / 2;
                if (n > 0) { var tmp = new short[n]; Marshal.Copy(b.Data, tmp, 0, n); dst.Write(tmp, n); total += n; }
                b.ClearRecorded();
                WinMm.waveInAddBuffer(_handle, b.Header, WinMm.HeaderSize);
                _next = (_next + 1) % _blocks.Count;
            }
            return total;
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero) WinMm.waveInReset(_handle);
            foreach (PcmBlock b in _blocks) { if (b.Prepared && _handle != IntPtr.Zero) WinMm.waveInUnprepareHeader(_handle, b.Header, WinMm.HeaderSize); b.Free(); }
            _blocks.Clear();
            if (_handle != IntPtr.Zero) { WinMm.waveInClose(_handle); _handle = IntPtr.Zero; }
        }
    }

    internal sealed class ShortFifo
    {
        private readonly short[] _data; private int _head, _count;
        public ShortFifo(int capacity) { _data = new short[capacity]; }
        public int Count { get { return _count; } }
        public void Clear() { _head = 0; _count = 0; }
        public void Write(short[] src, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (_count == _data.Length) { _head = (_head + 1) % _data.Length; _count--; }
                _data[(_head + _count) % _data.Length] = src[i]; _count++;
            }
        }
        public bool TryRead(out short value)
        {
            if (_count == 0) { value = 0; return false; }
            value = _data[_head]; _head = (_head + 1) % _data.Length; _count--; return true;
        }
    }

    internal sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;
        public static Biquad Notch(double f0, double q, double fs)
        {
            double w = 2 * Math.PI * f0 / fs, alpha = Math.Sin(w) / (2 * q), c = Math.Cos(w), a0 = 1 + alpha;
            return new Biquad { _b0 = 1 / a0, _b1 = -2 * c / a0, _b2 = 1 / a0, _a1 = -2 * c / a0, _a2 = (1 - alpha) / a0 };
        }
        public double Process(double x)
        {
            double y = _b0 * x + _z1; _z1 = _b1 * x - _a1 * y + _z2; _z2 = _b2 * x - _a2 * y; return y;
        }
    }

    internal sealed class TrcParams
    {
        public ToneProfile Tone;
        public int FunctionHz;
        public double ToneScale = 0.6;    // 0..1 master for tones
        public double MicGain = 1.4;      // linear drive into the limiter
        public bool Notch = true;
        public bool UseMic = true;
        public int MaxMs = 60000;
        public short[] Program;           // generated alert/paging audio (peak = AlertLevel/100 * 0.5)
        public double ProgramGain = 1.0;  // 0..1 relative to the voice ceiling
    }

    internal sealed class TrcEngine : IDisposable
    {
        public const int Rate = 48000;
        private const int Block = 480;    // 10 ms
        private MonoOutStream _out; private MonoInStream _mic;
        private Thread _thread; private volatile bool _stop;
        private readonly object _lock = new object();
        private Job _job;
        private readonly ShortFifo _fifo = new ShortFifo(Rate);
        private bool _timerRaised;
        public string Phase = "IDLE";
        public volatile int OutPeak, MicPeak;
        public long Underruns;
        public volatile bool Faulted;
        public string FaultText = "";
        // Tap of the voice signal as transmitted (mono 48 kHz), called on the audio thread.
        public Action<short[], int> VoiceTap;

        private sealed class Job
        {
            public int Kind;   // 0 function only, 1 keyed (tones + LLGT + voice), 2 voice only (no tones), 3 calibration
            public long Pos; public int G, F, VoiceStart, Tail, Max;
            public double GuardHz, FuncHz, GuardAmp, FuncAmp, HoldAmp, Ceil, MicGain, ProgGain, CalHz, CalAmp;
            public double PG, PF, PC, AmpG, AmpF, AmpC;
            public bool Llgt, UseMic, Notch, ToneSeq;
            public short[] Program; public int ProgPos;
            public volatile bool ReleaseRequested, Aborted; public long ReleaseAt = -1; public long EndAt = -1;
            public bool Done;
            public Biquad Filter;
        }

        public bool IsOpen { get { return _thread != null && !Faulted; } }
        public bool Active { get { lock (_lock) { return _job != null && !_job.Done; } } }

        public string OutputLayout { get { return _out == null ? "" : _out.Layout; } }
        public string Open(int outputId, int micId) { return Open(outputId, micId, TxJack.WholeDevice); }
        public string Open(int outputId, int micId, TxJack jack)
        {
            Close();
            try
            {
                _out = new MonoOutStream(Block); _out.Open(outputId, Rate, 6, jack);
                if (micId >= 0) { _mic = new MonoInStream(); _mic.Open(micId, Rate, 6, Block); }
                WinMm.timeBeginPeriod(1); _timerRaised = true;
                _stop = false; Faulted = false; FaultText = "";
                _thread = new Thread(Run); _thread.IsBackground = true; _thread.Name = "TRC audio"; _thread.Priority = ThreadPriority.AboveNormal; _thread.Start();
                return null;
            }
            catch (Exception ex) { string msg = ex.Message; Close(); return msg; }
        }

        public void Close()
        {
            _stop = true;
            lock (_lock) { if (_job != null) _job.Done = true; }
            if (_thread != null) { try { _thread.Join(800); } catch (Exception) { } _thread = null; }
            if (_mic != null) { try { _mic.Dispose(); } catch (Exception) { } _mic = null; }
            if (_out != null) { try { _out.Dispose(); } catch (Exception) { } _out = null; }
            if (_timerRaised) { WinMm.timeEndPeriod(1); _timerRaised = false; }
            lock (_lock) { _job = null; }
            Phase = "IDLE"; OutPeak = 0; MicPeak = 0;
        }
        public void Dispose() { Close(); }

        private static double Amp(double dbfs, double scale) { return scale * Math.Pow(10, dbfs / 20.0); }

        private Job Make(TrcParams p, int kind)
        {
            ToneProfile t = p.Tone; var j = new Job { Kind = kind };
            j.GuardHz = t.GuardHz; j.FuncHz = p.FunctionHz;
            j.G = t.SingleTone ? 0 : Rate * t.GuardMs / 1000; j.F = Rate * t.FunctionMs / 1000;
            j.GuardAmp = Amp(t.GuardDbfs, p.ToneScale); j.FuncAmp = Amp(t.FunctionDbfs, p.ToneScale); j.HoldAmp = Amp(t.HoldDbfs, p.ToneScale);
            j.Ceil = j.GuardAmp * Math.Pow(10, t.VoicePeakDb / 20.0);
            j.MicGain = p.MicGain; j.ProgGain = p.ProgramGain * j.Ceil / 0.5; j.UseMic = p.UseMic && _mic != null; j.Notch = p.Notch;
            j.Program = p.Program; j.Tail = Rate * t.TailMs / 1000; j.Max = (int)Math.Min((long)Rate * p.MaxMs / 1000, Int32.MaxValue);
            j.Llgt = kind == 1; j.ToneSeq = kind != 2;
            j.VoiceStart = kind == 2 ? Rate * 60 / 1000 : Math.Max(Rate * t.VoiceDelayMs / 1000, j.G + j.F);
            if (j.Notch) j.Filter = Biquad.Notch(t.GuardHz, 3.8, Rate);
            return j;
        }

        private bool Begin(Job j)
        {
            lock (_lock) { if (_job != null && !_job.Done) return false; _job = j; return true; }
        }
        public bool IsKeyedKind { get { lock (_lock) { return _job != null && !_job.Done && _job.Kind != 3; } } }

        // Guard + function tone only (channel/mode select, user function keys, monitor pulse).
        public bool SendFunction(TrcParams p) { return IsOpen && Begin(Make(p, 0)); }
        // Guard + function + LLGT + voice (or program) until Release(); program audio releases itself.
        public bool BeginKeyed(TrcParams p) { return IsOpen && Begin(Make(p, 1)); }
        // Voice only, no tones (used when PTT is sent over SB9600 instead).
        public bool BeginVoiceOnly(TrcParams p) { return IsOpen && Begin(Make(p, 2)); }
        // Steady tone for level calibration.
        public bool BeginCalibration(double hz, double dbfs, double scale, int maxMs)
        {
            if (!IsOpen) return false;
            var j = new Job { Kind = 3, CalHz = hz, CalAmp = Amp(dbfs, scale), Max = (int)Math.Min((long)Rate * maxMs / 1000, Int32.MaxValue) };
            return Begin(j);
        }
        public void Release() { lock (_lock) { if (_job != null) _job.ReleaseRequested = true; } }
        public void Abort() { lock (_lock) { if (_job != null) { _job.Aborted = true; _job.ReleaseRequested = true; } } }

        private void Run()
        {
            var buf = new short[Block]; var tmp = new short[Block];
            try
            {
                while (!_stop)
                {
                    if (_mic != null) _mic.ReadAll(_fifo);
                    if (!_out.HasFree) { Thread.Sleep(1); continue; }
                    Job job; lock (_lock) { job = _job; }
                    int peak = 0;
                    if (job == null || job.Done) { Array.Clear(buf, 0, Block); _fifo.Clear(); Phase = "IDLE"; }
                    else peak = Generate(job, buf, tmp);
                    OutPeak = Math.Max(peak, OutPeak * 9 / 10);
                    _out.Queue(buf);
                }
            }
            catch (Exception ex) { Faulted = true; FaultText = ex.Message; }
        }

        private int Generate(Job j, short[] buf, short[] voiceTap)
        {
            double twoPi = 2 * Math.PI; int peak = 0, micPeak = 0; int tapN = 0;
            bool voiceAtStart = (j.Kind == 1 || j.Kind == 2) && j.Pos >= j.VoiceStart && j.ReleaseAt < 0;
            if (!voiceAtStart && j.ReleaseAt < 0) _fifo.Clear();
            for (int i = 0; i < Block; i++)
            {
                long t = j.Pos;
                if (j.ReleaseRequested && j.ReleaseAt < 0) j.ReleaseAt = t;
                if (j.Kind != 0 && j.Kind != 3 && j.ReleaseAt < 0 && t >= j.Max) j.ReleaseAt = t;
                bool releasing = j.ReleaseAt >= 0;
                double tg = 0, tf = 0, tc = 0;
                if (j.Aborted) { if (j.EndAt < 0) j.EndAt = t + 240; }
                else if (j.Kind == 3)
                {
                    if (releasing || t >= j.Max) { if (j.EndAt < 0) j.EndAt = t + 240; } else tc = j.CalAmp;
                }
                else
                {
                    if (j.ToneSeq)
                    {
                        if (t < j.G) tg = j.GuardAmp;
                        else if (t < j.G + j.F) tf = j.FuncAmp;
                        else if (j.Llgt) { if (!(releasing && t >= Math.Max(j.ReleaseAt, j.G + j.F) + j.Tail)) tg = j.HoldAmp; }
                    }
                    if (j.Kind == 0 && t >= j.G + j.F + Rate / 50 && j.EndAt < 0) j.EndAt = t + 240;
                    if (j.Kind == 1 && releasing && t >= Math.Max(j.ReleaseAt, j.G + j.F) + j.Tail && j.EndAt < 0) j.EndAt = t + 240;
                    if (j.Kind == 2 && releasing && j.EndAt < 0) j.EndAt = t + 240;
                }
                double v = 0;
                if ((j.Kind == 1 || j.Kind == 2) && t >= j.VoiceStart && !releasing && !j.Aborted)
                {
                    if (j.Program != null)
                    {
                        if (j.ProgPos < j.Program.Length) { v = j.Program[j.ProgPos++] / 32768.0 * j.ProgGain; if (v > j.Ceil) v = j.Ceil; else if (v < -j.Ceil) v = -j.Ceil; }
                        else j.ReleaseAt = t;
                    }
                    else if (j.UseMic)
                    {
                        short s;
                        if (_fifo.TryRead(out s))
                        {
                            double x = s / 32768.0 * j.MicGain; if (j.Notch) x = j.Filter.Process(x);
                            double ax = Math.Abs(x); if (ax > micPeak / 32768.0) micPeak = (int)(ax * 32768.0);
                            v = Limit(x, j.Ceil);
                        }
                        else Underruns++;
                    }
                    if (tapN < voiceTap.Length) voiceTap[tapN++] = (short)Math.Round(Math.Max(-1.0, Math.Min(1.0, v)) * 32767.0);
                }
                j.AmpG += (tg - j.AmpG) * 0.03; j.AmpF += (tf - j.AmpF) * 0.03; j.AmpC += (tc - j.AmpC) * 0.03;
                double sample = j.AmpG * Math.Sin(j.PG) + j.AmpF * Math.Sin(j.PF) + j.AmpC * Math.Sin(j.PC) + v;
                if (sample > 0.98) sample = 0.98; else if (sample < -0.98) sample = -0.98;
                short outS = (short)Math.Round(sample * 32767.0); buf[i] = outS;
                int a = Math.Abs((int)outS); if (a > peak) peak = a;
                j.PG += twoPi * j.GuardHz / Rate; if (j.PG > twoPi) j.PG -= twoPi;
                j.PF += twoPi * j.FuncHz / Rate; if (j.PF > twoPi) j.PF -= twoPi;
                j.PC += twoPi * j.CalHz / Rate; if (j.PC > twoPi) j.PC -= twoPi;
                j.Pos = t + 1;
                if (j.EndAt >= 0 && j.Pos >= j.EndAt) { j.Done = true; for (int k = i + 1; k < Block; k++) buf[k] = 0; break; }
            }
            MicPeak = Math.Max(micPeak, MicPeak * 9 / 10);
            Phase = j.Kind == 3 ? "CAL" : j.ReleaseAt >= 0 ? "TAIL" : j.Pos < j.G ? "GUARD" : j.Pos < j.G + j.F ? "FUNCTION" : (j.Kind == 0 ? "IDLE" : "VOICE");
            if (tapN > 0 && VoiceTap != null) { try { VoiceTap(voiceTap, tapN); } catch (Exception) { } }
            return peak;
        }

        // Renders a job without any audio device (self-test and offline WAV). kind: 0 function, 1 keyed, 2 voice-only.
        public short[] RenderOffline(TrcParams p, int kind, int totalMs, int releaseMs)
        {
            if (IsOpen) throw new InvalidOperationException("RenderOffline needs an engine that is not open.");
            Job j = Make(p, kind); var all = new List<short>(); var buf = new short[Block]; var tap = new short[Block];
            int blocks = totalMs * Rate / 1000 / Block;
            for (int b = 0; b < blocks && !j.Done; b++)
            {
                if (releaseMs >= 0 && j.Pos >= (long)releaseMs * Rate / 1000) j.ReleaseRequested = true;
                Generate(j, buf, tap); all.AddRange(buf);
            }
            return all.ToArray();
        }

        // Soft limiter: linear to 60% of the ceiling, then a smooth knee that never exceeds the ceiling.
        public static double Limit(double x, double ceil)
        {
            double ax = Math.Abs(x), knee = ceil * 0.6;
            if (ax <= knee) return x;
            double over = (ax - knee) / (ceil - knee);
            double y = knee + (ceil - knee) * (1 - Math.Exp(-over));
            if (y > ceil) y = ceil;
            return x < 0 ? -y : y;
        }
    }
}
