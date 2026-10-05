using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.MediaFoundation;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace DemoRipper2026
{
    /// <summary>H.264 profiles by profile_idc. Main is Bandicam's and the default.</summary>
    public enum H264Profile { Baseline = 66, Main = 77, High = 100 }

    /// <summary>
    /// Records the BO2 window and BO2's own sound to an MP4 with no outside
    /// program. Windows.Graphics.Capture for the picture, WASAPI process
    /// loopback for the sound, Media Foundation (hardware H.264 when the GPU
    /// offers it) for the file.
    ///
    /// Prepare builds everything and starts capture without writing, so Start at
    /// the start tick only flips a switch. Video is written on a fixed clock:
    /// each frame slot takes the newest captured frame, so the file is a steady
    /// frame rate whatever BO2 draws.
    /// </summary>
    public sealed class BuiltInRecorder : IRecorder
    {
        [DllImport("winmm.dll")] private static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] private static extern int timeEndPeriod(int ms);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

        /// <summary>Process loopback arrived in Windows 10 build 20348.</summary>
        public const int ProcessLoopbackBuild = 20348;

        private static bool _mfStarted;

        private readonly object _lock = new object();       // state changes
        private readonly object _frameLock = new object();  // _latest
        private readonly string _processName;

        // Read when a take is prepared; volume is also read live.
        public int Fps = 60;
        public int Kbps = 0;                          // 0: pick from the game's resolution (AutoKbps)
        public H264Profile Profile = H264Profile.Main;
        private volatile float _gain = 1f;

        /// <summary>Seconds between keyframes. Bandicam's previews use 0.5, which keeps scrubbing quick.</summary>
        public const double KeyframeSeconds = 0.5;

        /// <summary>
        /// Preview bitrate for a game resolution: 7470 kbps (a common Bandicam setting)
        /// up to 1080p, 12000 at 1440p, 20000 at 4K. Bits per pixel fall as the
        /// resolution rises, which H.264 tolerates at higher resolutions, so previews
        /// stay previews-sized.
        /// </summary>
        public static int AutoKbps(int width, int height)
        {
            long pixels = (long)width * height;
            if (pixels <= 1920L * 1080) return 7470;
            if (pixels <= 2560L * 1440) return 12000;
            if (pixels <= 3840L * 2160) return 20000;
            return (int)(20000 * pixels / (3840L * 2160));
        }

        private D3D11Device _device;
        private WindowCapture _capture;
        private ProcessAudioCapture _audio;
        private Mp4Writer _writer;
        private Texture2D _latest;
        private bool _haveFrame;
        private Process _target;
        private string _preparedPath;   // without extension
        private string _file;

        private int _usedKbps;

        private volatile bool _recording;
        private long _startQpc;
        private Thread _pacer;
        private string _lastWarning;
        private string _lastStoppedPath;

        private long _framesWritten, _framesArrived, _framesRepeated;

        public BuiltInRecorder(string processName)
        {
            _processName = string.IsNullOrEmpty(processName) ? "t6mp" : processName;
        }

        public string Name { get { return "Built-in recorder"; } }
        public bool IsRecording { get { return _recording; } }
        public string LastWarning { get { return _lastWarning; } }
        public string LastStoppedPath { get { lock (_lock) return _lastStoppedPath; } }
        public bool IsPrepared { get { lock (_lock) return _writer != null; } }

        /// <summary>Game volume in the recording, in dB. 0 is as the game plays it.</summary>
        public double VolumeDb
        {
            set { _gain = (float)Math.Pow(10, value / 20.0); }
        }

        /// <summary>What the last take wrote, for the save log.</summary>
        public string Stats { get; private set; }

        private static long Qpc100ns()
        {
            return (long)(Stopwatch.GetTimestamp() * (10000000.0 / Stopwatch.Frequency));
        }

        public void Preflight()
        {
            if (_recording) throw new InvalidOperationException("The built-in recorder is already recording.");
            if (!global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
                throw new InvalidOperationException("This version of Windows can't capture a window. The built-in recorder needs Windows 10 1903 or later; use OBS instead.");
            FindTarget();
        }

        private Process FindTarget()
        {
            Process[] ps = Process.GetProcessesByName(_processName);
            if (ps.Length == 0) throw new InvalidOperationException("BO2 isn't running, so there's nothing to record.");
            Process p = ps[0];
            if (p.MainWindowHandle == IntPtr.Zero)
                throw new InvalidOperationException("BO2 has no window to record. Is it still starting?");
            // A minimised window captures as its 160×28 title bar with no frames.
            if (IsIconic(p.MainWindowHandle))
                throw new InvalidOperationException("BO2 is minimised, so there's no picture to record. Fullscreen BO2 minimises "
                    + "when another window is clicked: press numpad / from inside the game, or play in fullscreen windowed.");
            return p;
        }

        /// <summary>
        /// Builds the capture, sound and file for a take and starts frames
        /// flowing, without writing. Calling again for the same path does nothing.
        /// </summary>
        public void Prepare(string pathWithoutExtension)
        {
            lock (_lock)
            {
                if (_recording) throw new InvalidOperationException("Already recording.");
                if (_writer != null && _preparedPath == pathWithoutExtension) return;
                ReleaseLocked(true);

                _lastWarning = null;
                _target = FindTarget();
                string dir = Path.GetDirectoryName(pathWithoutExtension);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _file = pathWithoutExtension + ".mp4";

                try
                {
                    if (!_mfStarted)
                    {
                        MediaFactory.Startup(0x20070, 0);   // MF_VERSION, MFSTARTUP_FULL; once per process
                        _mfStarted = true;
                    }

                    // Capture callbacks and the frame clock share the device's context.
                    _device = new D3D11Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
                    using (var mt = _device.QueryInterface<Multithread>()) mt.SetMultithreadProtected(true);

                    _capture = new WindowCapture(_device, _target.MainWindowHandle);

                    try { _audio = new ProcessAudioCapture(_target.Id); }
                    catch (Exception ex)
                    {
                        _audio = null;
                        _lastWarning = Environment.OSVersion.Version.Build < ProcessLoopbackBuild
                            ? "Recording without sound: capturing one program's sound needs Windows 10 build " + ProcessLoopbackBuild + " or later."
                            : "Recording without sound: " + ex.GetBaseException().Message;
                    }

                    int fps = Math.Max(1, Math.Min(240, Fps));
                    int kbps = Kbps > 0 ? Math.Max(500, Math.Min(200000, Kbps)) : AutoKbps(_capture.Width, _capture.Height);
                    _usedKbps = kbps;
                    _writer = new Mp4Writer(_file, _device, _capture.Width, _capture.Height, fps, kbps,
                                            (int)Profile, (int)Math.Round(fps * KeyframeSeconds), _audio != null);

                    _latest = new Texture2D(_device, new Texture2DDescription
                    {
                        Width = _writer.Width,
                        Height = _writer.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None
                    });
                    _haveFrame = false;

                    _capture.Frame += OnFrame;
                    if (_audio != null) _audio.Packet += OnAudio;
                    _capture.Start();
                    if (_audio != null) _audio.Start();
                    _preparedPath = pathWithoutExtension;
                }
                catch
                {
                    ReleaseLocked(true);
                    throw;
                }
            }
        }

        public void Start(string pathWithoutExtension)
        {
            Prepare(pathWithoutExtension);
            lock (_lock)
            {
                _framesWritten = _framesArrived = _framesRepeated = 0;
                _lastStoppedPath = null;
                Interlocked.Exchange(ref _startQpc, Qpc100ns());
                _recording = true;
                _pacer = new Thread(Pace) { IsBackground = true, Name = "Recorder frame clock", Priority = ThreadPriority.AboveNormal };
                _pacer.Start();
            }
        }

        public string Stop()
        {
            Thread pacer;
            lock (_lock)
            {
                if (!_recording) return _lastStoppedPath;
                _recording = false;
                pacer = _pacer;
            }
            if (pacer != null && pacer != Thread.CurrentThread) pacer.Join(3000);
            return Finish(null);
        }

        /// <summary>Drops a prepared take that never started, and its empty file.</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                if (_recording) return;
                ReleaseLocked(true);
            }
        }

        private string Finish(string reason)
        {
            lock (_lock)
            {
                if (_writer == null) return _lastStoppedPath;
                string file = _file;
                string error = null;

                // Stop the sources first so nothing arrives mid-finalise.
                try { if (_audio != null) _audio.Dispose(); } catch { }
                _audio = null;
                try { if (_capture != null) _capture.Dispose(); } catch { }
                _capture = null;

                try { _writer.Finish(); }
                catch (Exception ex) { error = ex.GetBaseException().Message; }

                double seconds = _framesWritten / (double)Math.Max(1, Fps);
                Stats = "built-in  " + _writer.Width + "x" + _writer.Height + " @" + Fps + " fps, H.264 " + Profile + ", "
                        + _usedKbps + " kbps" + (Kbps > 0 ? "" : " (auto)") + ", "
                        + _framesWritten + " frames (" + seconds.ToString("0.0") + " s), "
                        + _framesArrived + " captured, " + _framesRepeated + " repeated";

                ReleaseLocked(false);
                _lastStoppedPath = error == null && File.Exists(file) ? file : null;
                if (error != null) _lastWarning = "The recording couldn't be finished: " + error;
                else if (reason != null) _lastWarning = reason;
                return _lastStoppedPath;
            }
        }

        private void ReleaseLocked(bool deleteFile)
        {
            try { if (_audio != null) _audio.Dispose(); } catch { }
            try { if (_capture != null) _capture.Dispose(); } catch { }
            lock (_frameLock)
            {
                if (_latest != null) _latest.Dispose();
                _latest = null;
                _haveFrame = false;
            }
            try { if (_writer != null) _writer.Dispose(); } catch { }
            try { if (_device != null) _device.Dispose(); } catch { }

            if (deleteFile && _file != null && _writer != null)
            {
                try { File.Delete(_file); } catch { }
            }

            _audio = null;
            _capture = null;
            _writer = null;
            _device = null;
            _preparedPath = null;
        }

        private void OnFrame(Texture2D texture, long time)
        {
            lock (_frameLock)
            {
                if (_latest == null) return;
                Texture2DDescription src = texture.Description;
                Texture2DDescription dst = _latest.Description;
                var context = _device.ImmediateContext;
                if (src.Width == dst.Width && src.Height == dst.Height)
                    context.CopyResource(texture, _latest);
                else
                    context.CopySubresourceRegion(texture, 0,
                        new ResourceRegion(0, 0, 0, Math.Min(dst.Width, src.Width), Math.Min(dst.Height, src.Height), 1), _latest, 0);
                _haveFrame = true;
                if (_recording) Interlocked.Increment(ref _framesArrived);
            }
        }

        private void OnAudio(byte[] buffer, int count, long time)
        {
            if (!_recording) return;
            long rel = time - Interlocked.Read(ref _startQpc);
            if (rel < 0) return;

            float gain = _gain;
            if (Math.Abs(gain - 1f) > 0.001f) ApplyGain(buffer, count, gain);

            try
            {
                Mp4Writer w = _writer;
                if (w != null) w.WriteAudio(buffer, count, rel);
            }
            catch { }
        }

        /// <summary>
        /// 16-bit PCM times a gain, with a soft knee above 80% so loud moments
        /// round off instead of clipping hard.
        /// </summary>
        public static void ApplyGain(byte[] buffer, int count, float gain)
        {
            const float Knee = 0.8f;
            for (int i = 0; i + 1 < count; i += 2)
            {
                float x = (short)(buffer[i] | (buffer[i + 1] << 8)) / 32768f * gain;
                float a = Math.Abs(x);
                if (a > Knee)
                {
                    a = Knee + (1 - Knee) * (float)Math.Tanh((a - Knee) / (1 - Knee));
                    x = x < 0 ? -a : a;
                }
                int v = (int)Math.Round(x * 32767f);
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                buffer[i] = (byte)v;
                buffer[i + 1] = (byte)(v >> 8);
            }
        }

        /// <summary>
        /// The frame clock. Slot n is written at n / fps with the newest frame.
        /// Late slots are caught up with the same frame; more than a second behind
        /// skips ahead rather than flooding the encoder.
        /// </summary>
        private void Pace()
        {
            timeBeginPeriod(1);
            try
            {
                long frame = 10000000L / Math.Max(1, Fps);
                long n = 0;
                long lastArrived = -1;
                int checkExit = 0;

                while (_recording)
                {
                    long now = Qpc100ns() - Interlocked.Read(ref _startQpc);
                    long due = n * frame;
                    if (now < due)
                    {
                        Thread.Sleep((int)Math.Max(1, Math.Min(10, (due - now) / 10000)));
                        continue;
                    }
                    if (now - due > 10000000L) { n = now / frame; continue; }

                    lock (_frameLock)
                    {
                        if (_haveFrame && _writer != null)
                        {
                            try
                            {
                                _writer.WriteVideo(_latest, due);
                                _framesWritten++;
                                long arrived = Interlocked.Read(ref _framesArrived);
                                if (arrived == lastArrived) _framesRepeated++;
                                lastArrived = arrived;
                            }
                            catch (Exception ex)
                            {
                                _lastWarning = "Video stopped writing: " + ex.GetBaseException().Message;
                            }
                        }
                    }
                    n++;

                    if (++checkExit >= Math.Max(1, Fps))
                    {
                        checkExit = 0;
                        Process target = _target;
                        if (target != null && target.HasExited)
                        {
                            lock (_lock) _recording = false;
                            Finish("BO2 closed, so the recording stopped there.");
                            return;
                        }
                    }
                }
            }
            finally { timeEndPeriod(1); }
        }

        public void Dispose()
        {
            if (_recording) { try { Stop(); } catch { } }
            else Cancel();
        }
    }
}
