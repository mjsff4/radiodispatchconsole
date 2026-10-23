using System;
using System.IO;
using System.Runtime.InteropServices;
namespace RadioDispatchConsole
{
    internal static class ConsoleWave
    {
        public const int Rate = 48000;
        public static bool ValidDigits(string digits)
        { foreach (char c in digits.ToUpperInvariant()) if ("123A456B789C*0#D".IndexOf(c) < 0) return false; return true; }
        public static short[] Alert(AlertProfile alert, int level)
        {
            int duration = alert.TotalMs;
            if (duration < 1 || duration > 60000) throw new ArgumentException("Alert duration is out of range.");
            var pcm = new short[(int)((long)Rate * duration / 1000)];
            double gain = Math.Min(100, Math.Max(0, level)) / 100.0 * 0.5;
            double phase = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                double ms = i * 1000.0 / Rate, frequency = alert.FrequencyA, value = 0;
                bool gate = true;
                switch (alert.Kind)
                {
                    case AlertKind.Beeper: gate = ms % 500 < 250; break;
                    case AlertKind.HiLo: frequency = ((int)(ms / 500) % 2 == 0) ? alert.FrequencyA : alert.FrequencyB; break;
                    case AlertKind.Sweep:
                        double position = (ms % 1500) / 1500.0;
                        frequency = alert.FrequencyA + (alert.FrequencyB - alert.FrequencyA) * (position < .5 ? position * 2 : (1 - position) * 2); break;
                    case AlertKind.TwoTone:
                        if (ms >= alert.ToneAMs && ms < alert.ToneAMs + alert.GapMs) gate = false;
                        if (ms >= alert.ToneAMs + alert.GapMs) frequency = alert.FrequencyB; break;
                    case AlertKind.Dtmf:
                        string digits = (alert.Digits ?? "").ToUpperInvariant();
                        int symbol = (int)ms / (150 + alert.GapMs);
                        if (symbol >= digits.Length || ms % (150 + alert.GapMs) >= 150) gate = false;
                        else
                        {
                            int index = "123A456B789C*0#D".IndexOf(digits[symbol]);
                            if (index < 0) throw new ArgumentException("Invalid DTMF digit.");
                            int[] rows = { 697, 770, 852, 941 }, columns = { 1209, 1336, 1477, 1633 };
                            value = .5 * (Math.Sin(2 * Math.PI * rows[index / 4] * i / Rate) + Math.Sin(2 * Math.PI * columns[index % 4] * i / Rate));
                        }
                        break;
                }
                if (alert.Kind != AlertKind.Dtmf) { value = Math.Sin(phase); phase = (phase + 2 * Math.PI * frequency / Rate) % (2 * Math.PI); }
                double envelope = Math.Min(1, Math.Min(i / 240.0, (pcm.Length - 1 - i) / 240.0));
                pcm[i] = gate ? (short)Math.Round(32767 * gain * envelope * value) : (short)0;
            }
            return pcm;
        }
        public static short[] ToneSequence(RadioProfile radio)
        {
            ToneProfile t = radio.Tone; ChannelProfile channel = radio.Channels[radio.SelectedChannel];
            int lead = t.SingleTone ? 0 : t.GuardMs;
            int quiet = Math.Max(0, t.VoiceDelayMs - lead - t.FunctionMs);
            int length = lead + t.FunctionMs + quiet + 1000 + 100;
            var pcm = new short[Rate * length / 1000];
            double scale = radio.ToneLevel / 100.0;
            int pos = 0;
            if (lead > 0) pos = Sine(pcm, pos, t.GuardHz, lead, scale * Math.Pow(10, t.GuardDbfs / 20));
            pos = Sine(pcm, pos, channel.FunctionHz, t.FunctionMs, scale * Math.Pow(10, t.FunctionDbfs / 20));
            // Low guard occupies the hold/voice-delay region in this offline design waveform.
            Sine(pcm, pos, t.GuardHz, quiet + 1000, scale * Math.Pow(10, t.HoldDbfs / 20));
            return pcm;
        }
        private static int Sine(short[] pcm, int position, double hz, int ms, double gain)
        {
            int count = Rate * ms / 1000;
            for (int i = 0; i < count; i++) pcm[position + i] = (short)Math.Round(32767 * gain * Math.Sin(2 * Math.PI * hz * i / Rate));
            return position + count;
        }
        public static void Write(string path, short[] samples) { Write(path, samples, Rate); }
        public static void Write(string path, short[] samples, int rate)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(new char[] { 'R','I','F','F' }); writer.Write(36 + samples.Length * 2);
                writer.Write(new char[] { 'W','A','V','E','f','m','t',' ' }); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
                writer.Write((short)2); writer.Write((short)16); writer.Write(new char[] { 'd','a','t','a' }); writer.Write(samples.Length * 2);
                foreach (short sample in samples) writer.Write(sample);
            }
        }
    }
    // Local playback on the operator's speaker only (alert audition, instant recall). Never routed to a radio.
    internal sealed class LocalPreview : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct Format { public ushort tag, channels; public uint rate, bytes; public ushort align, bits, extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Header { public IntPtr data; public uint length, recorded; public UIntPtr user; public uint flags, loops; public IntPtr next; public UIntPtr reserved; }
        private IntPtr _handle, _header, _data;
        private bool _prepared;
        private static readonly uint Size = (uint)Marshal.SizeOf(typeof(Header));
        private static void Check(uint code) { if (code != 0) throw new InvalidOperationException("Local preview audio error " + code + ". Check output selection."); }
        public bool Finished { get { return _header == IntPtr.Zero || (((Header)Marshal.PtrToStructure(_header, typeof(Header))).flags & 1) != 0; } }
        public void Start(int deviceId, short[] samples) { Start(deviceId, samples, ConsoleWave.Rate); }
        public void Start(int deviceId, short[] samples, int rate)
        {
            if (_handle != IntPtr.Zero) throw new InvalidOperationException("Stop the previous preview first.");
            if (samples == null || samples.Length == 0) throw new InvalidOperationException("Nothing to play.");
            var format = new Format { tag = 1, channels = 1, rate = (uint)rate, bytes = (uint)rate * 2, align = 2, bits = 16 };
            try
            {
                Check(waveOutOpen(out _handle, (uint)deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0));
                _data = Marshal.AllocHGlobal(samples.Length * 2); Marshal.Copy(samples, 0, _data, samples.Length);
                _header = Marshal.AllocHGlobal((int)Size); Marshal.StructureToPtr(new Header { data = _data, length = (uint)samples.Length * 2 }, _header, false);
                Check(waveOutPrepareHeader(_handle, _header, Size)); _prepared = true;
                Check(waveOutWrite(_handle, _header, Size));
            }
            catch { try { Dispose(); } catch (Exception) { } throw; }
        }
        public void Dispose()
        {
            if (_handle != IntPtr.Zero) Check(waveOutReset(_handle));
            if (_prepared) { Check(waveOutUnprepareHeader(_handle, _header, Size)); _prepared = false; }
            if (_header != IntPtr.Zero) { Marshal.FreeHGlobal(_header); _header = IntPtr.Zero; }
            if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }
            if (_handle != IntPtr.Zero) { Check(waveOutClose(_handle)); _handle = IntPtr.Zero; }
        }
        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
    }
}
