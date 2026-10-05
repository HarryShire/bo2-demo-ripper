using System;
using System.Runtime.InteropServices;

namespace DemoRipper2026
{
    /// <summary>
    /// The window's title bar in the walleye theme. Windows 11 lets an app colour its own caption, caption text and border
    /// (DwmSetWindowAttribute 34-36); Windows 10 ignores those, and gets the dark title bar (20) instead.
    /// </summary>
    public partial class MainWindow
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34,
                          DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

        /// <summary>COLORREF is 0x00BBGGRR.</summary>
        private static int Rgb(byte r, byte g, byte b) { return r | (g << 8) | (b << 16); }

        private static void ThemeTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
                int caption = Rgb(0x15, 0x12, 0x0B), text = Rgb(0xE6, 0xD6, 0xAE), border = Rgb(0x9C, 0x7A, 0x3C);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, 4);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, 4);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, 4);
            }
            catch { }   // older Windows: the default title bar
        }
    }
}
