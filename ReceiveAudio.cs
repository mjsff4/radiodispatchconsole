// Independent RX resource. No tone generation, serial writes, PTT or radio TX routing.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RadioDispatchConsole
{
    internal sealed class AudioDevice
    {
        public readonly int Id;
        public readonly string Name;
        public AudioDevice(int id, string name) { Id = id; Name = name; }
        public override string ToString() { return Id < 0 ? Name : (Id + 1) + ": " + Name; }
    }

    internal static class MonitorPcm
    {
        // Returns input peak before monitor gain; modifies only this app's copied PCM.
        public static int Apply(short[] samples, int count, int volume, bool mute)
        {
            int peak = 0;
            double gain = Math.Max(0, Math.Min(100, volume)) / 100.0;
            for (int i = 0; i < count; i++)
            {
                int s = samples[i];
                peak = Math.Max(peak, Math.Abs(s));
                samples[i] = mute ? (short)0 : (short)Math.Round(s * gain);
            }
            return peak;
        }
    }

    internal sealed class ReceiveAudio : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct Format
        {
            public ushort tag, channels;
            public uint rate, bytesPerSecond;
            public ushort align, bits, extra;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            public IntPtr data;
            public uint length, recorded;
            public UIntPtr user;
            public uint flags, loops;
            public IntPtr next;
            public UIntPtr reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OutputCaps
        {
            public ushort manufacturer, product;
            public uint version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
            public uint formats;
            public ushort channels, reserved;
            public uint support;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct InputCaps
        {
            public ushort manufacturer, product;
            public uint version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
            public uint formats;
            public ushort channels, reserved;
        }
        private sealed class Block
        {
            public IntPtr Header, Data;
            public bool Prepared, Queued;
            public Block(int bytes)
            {
                Data = Marshal.AllocHGlobal(bytes);
                try
                {
                    Header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Header)));
                    Marshal.StructureToPtr(new Header { data = Data, length = (uint)bytes }, Header, false);
                }
                catch { Marshal.FreeHGlobal(Data); throw; }
            }
            public Header Read() { return (Header)Marshal.PtrToStructure(Header, typeof(Header)); }
            public void Free() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
        }
        private const int BlockBytes = 7680; // 40 ms, 48 kHz stereo PCM16
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(Header));
        private IntPtr _input, _output;
        private readonly List<Block> _in = new List<Block>(), _out = new List<Block>();
        private readonly short[] _samples = new short[BlockBytes / 2];
        private int _nextIn, _nextOut;
        public int Volume = 70;
        public bool Muted;
        public int Peak { get; private set; }
        public long DroppedBlocks { get; private set; }
        public bool Running { get; private set; }
        // Receives a mono (L+R average) 48 kHz copy of every captured block, before monitor gain. UI thread.
        public Action<short[], int> Tap;
        public static List<AudioDevice> Devices(bool input)
        {
            var list = new List<AudioDevice>();
            // Explicit output selection is required; never silently switch to default.
            uint count = input ? waveInGetNumDevs() : waveOutGetNumDevs();
            for (uint id = 0; id < count; id++)
            {
                if (input)
                {
                    InputCaps caps;
                    if (waveInGetDevCapsW(new UIntPtr(id), out caps, (uint)Marshal.SizeOf(typeof(InputCaps))) == 0)
                        list.Add(new AudioDevice((int)id, caps.name));
                }
                else
                {
                    OutputCaps caps;
                    if (waveOutGetDevCapsW(new UIntPtr(id), out caps, (uint)Marshal.SizeOf(typeof(OutputCaps))) == 0)
                        list.Add(new AudioDevice((int)id, caps.name));
                }
            }
            return list;
        }
        private static void Check(uint code, string action)
        {
            if (code != 0) throw new InvalidOperationException(action + " failed (Windows audio code " + code + "). Check the selected devices and sound settings.");
        }
        public void Start(int inputId, int outputId)
        {
            if (Running || _input != IntPtr.Zero || _output != IntPtr.Zero) throw new InvalidOperationException("Stop the existing monitor first.");
            if (inputId < 0 || outputId < 0) throw new InvalidOperationException("Select an explicit input and output.");
            var format = new Format { tag = 1, channels = 2, rate = 48000, bytesPerSecond = 192000, align = 4, bits = 16 };
            try
            {
                Check(waveInOpen(out _input, (uint)inputId, ref format, IntPtr.Zero, IntPtr.Zero, 0), "Open input");
                Check(waveOutOpen(out _output, (uint)outputId, ref format, IntPtr.Zero, IntPtr.Zero, 0), "Open output");
                for (int i = 0; i < 6; i++)
                {
                    var capture = new Block(BlockBytes); _in.Add(capture);
                    Check(waveInPrepareHeader(_input, capture.Header, HeaderSize), "Prepare input"); capture.Prepared = true;
                    Check(waveInAddBuffer(_input, capture.Header, HeaderSize), "Queue input"); capture.Queued = true;
                    var playback = new Block(BlockBytes); _out.Add(playback);
                    Check(waveOutPrepareHeader(_output, playback.Header, HeaderSize), "Prepare output"); playback.Prepared = true;
                }
                _nextIn = _nextOut = 0; DroppedBlocks = 0;
                Check(waveInStart(_input), "Start input");
                Running = true;
            }
            catch { try { Dispose(); } catch (Exception) { } throw; }
        }
        // Called only on the UI thread; no native callbacks outlive a managed delegate.
        public void Pump()
        {
            if (!Running) return;
            int peak = 0;
            for (int n = 0; n < _in.Count; n++)
            {
                Block capture = _in[_nextIn];
                Header h = capture.Read();
                if ((h.flags & 1) == 0) break;
                int count = (int)Math.Min(h.recorded, (uint)BlockBytes) / 4 * 2;
                Marshal.Copy(capture.Data, _samples, 0, count);
                if (Tap != null && count >= 2)
                {
                    var mono = new short[count / 2];
                    for (int m = 0; m < mono.Length; m++) mono[m] = (short)((_samples[2 * m] + _samples[2 * m + 1]) / 2);
                    try { Tap(mono, mono.Length); } catch (Exception) { }
                }
                peak = Math.Max(peak, MonitorPcm.Apply(_samples, count, Volume, Muted));
                Block playback = _out[_nextOut];
                if (count > 0 && (!playback.Queued || (playback.Read().flags & 1) != 0))
                {
                    // All normal buffers are full size. Clear the unused tail on a short input.
                    Array.Clear(_samples, count, _samples.Length - count);
                    Marshal.Copy(_samples, 0, playback.Data, _samples.Length);
                    Check(waveOutWrite(_output, playback.Header, HeaderSize), "Play monitor audio");
                    playback.Queued = true; _nextOut = (_nextOut + 1) % _out.Count;
                }
                else if (count > 0) DroppedBlocks++;
                // Preserve driver's prepared header fields; reset only recorded count.
                Marshal.WriteInt32(capture.Header, (int)Marshal.OffsetOf(typeof(Header), "recorded"), 0);
                Check(waveInAddBuffer(_input, capture.Header, HeaderSize), "Requeue input");
                _nextIn = (_nextIn + 1) % _in.Count;
            }
            Peak = Math.Max(peak, (int)(Peak * 0.82));
        }
        public void Dispose()
        {
            Running = false; Peak = 0;
            // Never free memory until the driver has returned and unprepared it.
            if (_input != IntPtr.Zero) Check(waveInReset(_input), "Reset input");
            if (_output != IntPtr.Zero) Check(waveOutReset(_output), "Reset output");
            Release(_in, true); Release(_out, false);
            if (_input != IntPtr.Zero) { Check(waveInClose(_input), "Close input"); _input = IntPtr.Zero; }
            if (_output != IntPtr.Zero) { Check(waveOutClose(_output), "Close output"); _output = IntPtr.Zero; }
        }
        private void Release(List<Block> blocks, bool input)
        {
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                Block block = blocks[i];
                if (block.Prepared)
                    Check(input ? waveInUnprepareHeader(_input, block.Header, HeaderSize) : waveOutUnprepareHeader(_output, block.Header, HeaderSize), "Release audio buffer");
                block.Free(); blocks.RemoveAt(i);
            }
        }
        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCapsW(UIntPtr id, out InputCaps caps, uint size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveOutGetDevCapsW(UIntPtr id, out OutputCaps caps, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
    }
}
