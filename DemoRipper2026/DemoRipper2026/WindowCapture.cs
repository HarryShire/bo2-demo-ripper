using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using SharpDX.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace DemoRipper2026
{
    // The interop declarations and the device and capture-item helpers follow Microsoft's
    // WPF ScreenCapture sample (Windows.UI.Composition-Win32-Samples, dotnet/WPF/ScreenCapture).
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    /// <summary>Windows.Graphics.Capture on one window, frames handed out as D3D11 textures.</summary>
    sealed class WindowCapture : IDisposable
    {
        static readonly Guid GraphicsCaptureItemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        static readonly Guid Texture2DIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true,
                   CallingConvention = CallingConvention.StdCall)]
        static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        readonly IDirect3DDevice _winrtDevice;
        readonly GraphicsCaptureItem _item;
        readonly Direct3D11CaptureFramePool _pool;
        readonly GraphicsCaptureSession _session;
        global::Windows.Graphics.SizeInt32 _poolSize;

        public readonly int Width, Height;
        public string BorderNote = "";

        /// <summary>Texture is only valid during the call. Time is QPC in 100 ns units.</summary>
        public event Action<Texture2D, long> Frame;

        public WindowCapture(D3D11Device device, IntPtr hwnd)
        {
            _winrtDevice = CreateWinrtDevice(device);
            _item = CreateItemForWindow(hwnd);
            Width = _item.Size.Width;
            Height = _item.Size.Height;
            _poolSize = _item.Size;

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
            _session = _pool.CreateCaptureSession(_item);

            try { _session.IsCursorCaptureEnabled = false; } catch { }
            try
            {
                var status = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().Result;
                _session.IsBorderRequired = false;
                BorderNote = "border off (access " + status + ")";
            }
            catch (Exception ex) { BorderNote = "border stays on: " + ex.GetBaseException().Message; }

            _pool.FrameArrived += OnFrameArrived;
        }

        public void Start()
        {
            _session.StartCapture();
        }

        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            using (Direct3D11CaptureFrame f = sender.TryGetNextFrame())
            {
                if (f == null) return;

                // The window changed size: frames keep the old size until the pool
                // is rebuilt. The writer's size stays fixed; it copies what fits.
                var size = f.ContentSize;
                if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                {
                    _poolSize = size;
                    try { sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size); }
                    catch { }
                }

                var access = (IDirect3DDxgiInterfaceAccess)(object)f.Surface;
                Guid iid = Texture2DIid;
                IntPtr p = access.GetInterface(ref iid);
                using (var tex = new Texture2D(p))
                {
                    Action<Texture2D, long> h = Frame;
                    if (h != null) h(tex, f.SystemRelativeTime.Ticks);
                }
            }
        }

        static IDirect3DDevice CreateWinrtDevice(D3D11Device device)
        {
            using (var dxgi = device.QueryInterface<SharpDX.DXGI.Device>())
            {
                IntPtr ptr;
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out ptr);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                var result = (IDirect3DDevice)Marshal.GetObjectForIUnknown(ptr);
                Marshal.Release(ptr);
                return result;
            }
        }

        static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        {
            var factory = WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            var interop = (IGraphicsCaptureItemInterop)factory;
            Guid iid = GraphicsCaptureItemIid;
            IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iid);
            var item = (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }

        public void Dispose()
        {
            _pool.FrameArrived -= OnFrameArrived;
            _session.Dispose();
            _pool.Dispose();
        }
    }
}
