using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DemoRipper2026
{
    // Process loopback, following Microsoft's ApplicationLoopback sample (Windows-classic-samples)
    // and the docs for ActivateAudioInterfaceAsync / AUDIOCLIENT_ACTIVATION_PARAMS.

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
                                     [In] ref WaveFormatEx format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioClientActivationParams
    {
        public int ActivationType;          // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
        public uint TargetProcessId;
        public int ProcessLoopbackMode;     // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariantBlob
    {
        public ushort Vt, Reserved1, Reserved2, Reserved3;
        public uint Size;
        public IntPtr Data;
    }

    [ComVisible(true)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        public int Result = -1;
        public IAudioClient Client;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            int hr;
            object iface;
            operation.GetActivateResult(out hr, out iface);
            Result = hr;
            Client = iface as IAudioClient;
            Done.Set();
        }
    }

    /// <summary>Captures only the audio one process renders, as 16-bit stereo PCM at the writer's rate (Windows converts).</summary>
    sealed class ProcessAudioCapture : IDisposable
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        static extern IActivateAudioInterfaceAsyncOperation ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, [In] ref Guid riid,
            [In] IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler);

        const string ProcessLoopbackDevice = @"VAD\Process_Loopback";
        const int StreamFlagsLoopback = 0x00020000, StreamFlagsEventCallback = 0x00040000;
        const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
        const uint BufferFlagsSilent = 0x2;

        readonly IAudioClient _client;
        readonly IAudioCaptureClient _capture;
        readonly AutoResetEvent _ready = new AutoResetEvent(false);
        Thread _thread;
        volatile bool _stop;

        public long Packets, SilentPackets, Discontinuities;

        /// <summary>PCM bytes, count, QPC time of the first frame in 100 ns units.</summary>
        public event Action<byte[], int, long> Packet;

        public ProcessAudioCapture(int processId)
        {
            var activation = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = (uint)processId, ProcessLoopbackMode = 0 };
            IntPtr pActivation = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AudioClientActivationParams)));
            IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PropVariantBlob)));
            try
            {
                Marshal.StructureToPtr(activation, pActivation, false);
                var variant = new PropVariantBlob { Vt = 65 /* VT_BLOB */, Size = (uint)Marshal.SizeOf(typeof(AudioClientActivationParams)), Data = pActivation };
                Marshal.StructureToPtr(variant, pVariant, false);

                var handler = new ActivationHandler();
                Guid iid = typeof(IAudioClient).GUID;
                IActivateAudioInterfaceAsyncOperation op = ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, pVariant, handler);
                if (!handler.Done.Wait(5000)) throw new TimeoutException("Process loopback activation didn't complete.");
                GC.KeepAlive(op);
                if (handler.Result != 0) Marshal.ThrowExceptionForHR(handler.Result);
                _client = handler.Client;
            }
            finally
            {
                Marshal.FreeHGlobal(pVariant);
                Marshal.FreeHGlobal(pActivation);
            }

            var format = new WaveFormatEx
            {
                FormatTag = 1,
                Channels = 2,
                SamplesPerSec = (uint)Mp4Writer.AudioRate,
                BitsPerSample = 16,
                BlockAlign = 4,
                AvgBytesPerSec = (uint)Mp4Writer.AudioRate * 4,
                Size = 0
            };
            Check(_client.Initialize(0, StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm, 0, 0, ref format, IntPtr.Zero), "Initialize");
            Check(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");

            Guid captureIid = typeof(IAudioCaptureClient).GUID;
            object service;
            Check(_client.GetService(ref captureIid, out service), "GetService");
            _capture = (IAudioCaptureClient)service;
        }

        public void Start()
        {
            Check(_client.Start(), "Start");
            _thread = new Thread(Loop) { IsBackground = true, Name = "Process loopback" };
            _thread.Start();
        }

        void Loop()
        {
            byte[] buffer = new byte[Mp4Writer.AudioRate * 4];
            while (!_stop)
            {
                _ready.WaitOne(100);
                while (!_stop)
                {
                    uint next;
                    if (_capture.GetNextPacketSize(out next) != 0 || next == 0) break;

                    IntPtr data;
                    uint frames, flags;
                    ulong devicePosition, qpc;
                    if (_capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc) != 0) break;

                    int bytes = (int)frames * 4;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((flags & BufferFlagsSilent) != 0) { Array.Clear(buffer, 0, bytes); SilentPackets++; }
                    else Marshal.Copy(data, buffer, 0, bytes);
                    if ((flags & 0x1) != 0) Discontinuities++;
                    Packets++;

                    Action<byte[], int, long> h = Packet;
                    if (h != null) h(buffer, bytes, (long)qpc);
                    _capture.ReleaseBuffer(frames);
                }
            }
        }

        static void Check(int hr, string what)
        {
            if (hr != 0) throw new COMException("IAudioClient." + what + " failed", hr);
        }

        public void Dispose()
        {
            _stop = true;
            if (_thread != null) _thread.Join(1000);
            try { _client.Stop(); } catch { }
        }
    }
}
