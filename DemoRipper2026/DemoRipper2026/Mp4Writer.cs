using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.MediaFoundation;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace DemoRipper2026
{
    /// <summary>
    /// Media Foundation sink writer: GPU textures in, H.264 (hardware MFT through
    /// the DXGI device manager) + AAC out, into an MP4.
    /// </summary>
    sealed class Mp4Writer : IDisposable
    {
        static readonly Guid Texture2DIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        // MF_MT_MPEG2_PROFILE: the H.264 profile on the encoder's output type.
        // https://learn.microsoft.com/windows/win32/medfound/mf-mt-mpeg2-profile-attribute
        static readonly MediaAttributeKey<int> Mpeg2Profile = new MediaAttributeKey<int>("ad76a80b-2d5c-4e0b-b375-64e520137036");

        // CODECAPI_AVEncMPVGOPSize: frames between keyframes, passed to the encoder
        // through SetInputMediaType's encoding parameters.
        // https://learn.microsoft.com/windows/win32/directshow/avencmpvgopsize
        static readonly MediaAttributeKey<int> GopSize = new MediaAttributeKey<int>("95f31b26-95a4-41aa-9303-246a7fc6eef1");

        // Matches Bandicam's previews: AAC-LC, 48 kHz, 192 kbps.
        public const int AudioRate = 48000, AudioChannels = 2, AudioBits = 16;
        const int AacBytesPerSecond = 24000;

        readonly D3D11Device _device;
        readonly DXGIDeviceManager _manager;
        readonly SinkWriter _writer;
        readonly object _lock = new object();
        readonly int _video = -1, _audio = -1;
        readonly long _frameDuration;
        bool _closed;           // after Finish, late samples from the capture threads are dropped

        public readonly int Width, Height;
        public string VideoInput = "";

        /// <param name="profile">H.264 profile_idc: 66 Baseline, 77 Main, 100 High.</param>
        /// <param name="gopFrames">Frames between keyframes; 0 leaves it to the encoder.</param>
        public Mp4Writer(string path, D3D11Device device, int width, int height, int fps, int kbps,
                         int profile, int gopFrames, bool withAudio)
        {
            _device = device;
            Width = width & ~1;     // H.264 wants even dimensions
            Height = height & ~1;
            _frameDuration = 10000000L / fps;

            _manager = new DXGIDeviceManager();
            _manager.ResetDevice(device);

            using (var attrs = new MediaAttributes(4))
            {
                attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1);
                attrs.Set(SinkWriterAttributeKeys.D3DManager, _manager);
                attrs.Set(SinkWriterAttributeKeys.DisableThrottling, 1);
                _writer = MediaFactory.CreateSinkWriterFromURL(path, null, attrs);
            }

            long frameSize = ((long)Width << 32) | (uint)Height;
            long frameRate = ((long)fps << 32) | 1u;
            long square = (1L << 32) | 1u;

            using (var output = new MediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                output.Set(MediaTypeAttributeKeys.AvgBitrate, kbps * 1000);
                output.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                output.Set(MediaTypeAttributeKeys.FrameSize, frameSize);
                output.Set(MediaTypeAttributeKeys.FrameRate, frameRate);
                output.Set(MediaTypeAttributeKeys.PixelAspectRatio, square);
                output.Set(Mpeg2Profile, profile);
                _writer.AddStream(output, out _video);
            }

            var encoding = new MediaAttributes(1);
            if (gopFrames > 0) encoding.Set(GopSize, gopFrames);

            Exception last = null;
            foreach (var sub in new[] { VideoFormatGuids.Argb32, VideoFormatGuids.Rgb32 })
            {
                try
                {
                    using (var input = new MediaType())
                    {
                        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        input.Set(MediaTypeAttributeKeys.Subtype, sub);
                        input.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                        input.Set(MediaTypeAttributeKeys.FrameSize, frameSize);
                        input.Set(MediaTypeAttributeKeys.FrameRate, frameRate);
                        input.Set(MediaTypeAttributeKeys.PixelAspectRatio, square);
                        _writer.SetInputMediaType(_video, input, encoding);
                    }
                    VideoInput = sub == VideoFormatGuids.Argb32 ? "ARGB32" : "RGB32";
                    last = null;
                    break;
                }
                catch (Exception ex) { last = ex; }
            }
            encoding.Dispose();
            if (last != null) throw new InvalidOperationException("No RGB input type accepted: " + last.Message, last);

            if (withAudio)
            {
                using (var output = new MediaType())
                {
                    output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioRate);
                    output.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
                    output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBits);
                    output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AacBytesPerSecond);
                    _writer.AddStream(output, out _audio);
                }
                using (var input = new MediaType())
                {
                    input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, AudioRate);
                    input.Set(MediaTypeAttributeKeys.AudioNumChannels, AudioChannels);
                    input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, AudioBits);
                    input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, AudioChannels * AudioBits / 8);
                    input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, AudioRate * AudioChannels * AudioBits / 8);
                    _writer.SetInputMediaType(_audio, input, null);
                }
            }

            _writer.BeginWriting();
        }

        public void WriteVideo(Texture2D source, long time)
        {
            Texture2DDescription src = source.Description;
            var desc = new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };

            // The capture frame goes back to its pool when the handler returns, and
            // the encoder works asynchronously, so each sample gets its own copy.
            using (var copy = new Texture2D(_device, desc))
            {
                if (src.Width == Width && src.Height == Height)
                    _device.ImmediateContext.CopyResource(source, copy);
                else
                    _device.ImmediateContext.CopySubresourceRegion(source, 0,
                        new ResourceRegion(0, 0, 0, Math.Min(Width, src.Width), Math.Min(Height, src.Height), 1), copy, 0);

                MediaBuffer buffer;
                MediaFactory.CreateDXGISurfaceBuffer(Texture2DIid, copy, 0, false, out buffer);
                using (buffer)
                using (var b2 = buffer.QueryInterface<Buffer2D>())
                {
                    buffer.CurrentLength = b2.ContiguousLength;
                    using (Sample sample = MediaFactory.CreateSample())
                    {
                        sample.AddBuffer(buffer);
                        sample.SampleTime = time;
                        sample.SampleDuration = _frameDuration;
                        lock (_lock) { if (!_closed) _writer.WriteSample(_video, sample); }
                    }
                }
            }
        }

        public void WriteAudio(byte[] data, int count, long time)
        {
            if (_audio < 0 || count <= 0) return;
            using (MediaBuffer buffer = MediaFactory.CreateMemoryBuffer(count))
            {
                int max, cur;
                IntPtr p = buffer.Lock(out max, out cur);
                Marshal.Copy(data, 0, p, count);
                buffer.Unlock();
                buffer.CurrentLength = count;

                int frames = count / (AudioChannels * AudioBits / 8);
                using (Sample sample = MediaFactory.CreateSample())
                {
                    sample.AddBuffer(buffer);
                    sample.SampleTime = time;
                    sample.SampleDuration = frames * 10000000L / AudioRate;
                    lock (_lock) { if (!_closed) _writer.WriteSample(_audio, sample); }
                }
            }
        }

        /// <summary>
        /// Which encoder the sink writer actually loaded for video. GetServiceForStream
        /// on a stream index asks that stream's encoder first (docs: IMFSinkWriter::
        /// GetServiceForStream). Hardware MFTs carry MFT_ENUM_HARDWARE_URL_Attribute.
        /// Call after frames have been written: the encoder is created lazily.
        /// </summary>
        public string EncoderInfo()
        {
            try
            {
                IntPtr p;
                lock (_lock) _writer.GetServiceForStream(_video, Guid.Empty, typeof(Transform).GUID, out p);
                using (var transform = new Transform(p))
                {
                    MediaAttributes a = transform.Attributes;
                    string name = "", url = "";
                    try { name = a.Get(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                    try { url = a.Get(TransformAttributeKeys.MftEnumHardwareUrlAttribute); } catch { }
                    return (name.Length > 0 ? name : "(no friendly name)")
                           + (url.Length > 0 ? ", HARDWARE (" + url + ")" : ", no hardware URL: software MFT");
                }
            }
            catch (Exception ex) { return "encoder query failed: " + ex.Message; }
        }

        public void Finish()
        {
            lock (_lock) { if (_closed) return; _closed = true; _writer.Finalize(); }
        }

        public void Dispose()
        {
            lock (_lock) _closed = true;
            _writer.Dispose();
            _manager.Dispose();
        }
    }
}
