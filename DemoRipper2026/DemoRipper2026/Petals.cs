using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DemoRipper2026
{
    /// <summary>
    /// Background sparks: a low-res, pixel-art particle animation in gold, rising
    /// like fire sparks; feathered, quiet and light on the CPU.
    ///
    /// A canvas at a fifth of the window's size, scaled up with hard pixel edges. Sparks rise from the
    /// bottom with a little sideways drift, flicker, leave a dim one-pixel trail and burn out on the way
    /// up. 10 frames a second, a few thousand pixels a frame; stops while the window is minimised.
    /// </summary>
    public partial class MainWindow
    {
        private const int PetalScale = 5, PetalCount = 24, PetalFps = 10;
        private WriteableBitmap _petalBmp;
        private int[] _petalPix;
        private int _pw, _ph;
        private readonly Random _rng = new Random();
        private DispatcherTimer _petalTimer;

        private struct Spark { public double X, Y, Vx, Vy, Phase, Wobble, Life, Age; public int Tone; }
        private readonly Spark[] _sparks = new Spark[PetalCount];

        // pale gold core, a deeper gold, a warm ember amber
        private static readonly int[] SparkTones = { 0xF0DFB0, 0xC9A45C, 0xE0903A };

        private void StartPetals()
        {
            SizeChanged += (s, e) => ResizePetals();
            StateChanged += (s, e) => { if (_petalTimer != null) _petalTimer.IsEnabled = WindowState != WindowState.Minimized; };
            ResizePetals();
            for (int i = 0; i < PetalCount; i++) { NewSpark(ref _sparks[i]); _sparks[i].Age = _rng.NextDouble() * _sparks[i].Life; }
            _petalTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000 / PetalFps) };
            _petalTimer.Tick += (s, e) => StepPetals();
            _petalTimer.Start();
        }

        private void ResizePetals()
        {
            int w = Math.Max(16, (int)(ActualWidth / PetalScale)), h = Math.Max(16, (int)(ActualHeight / PetalScale));
            if (w == _pw && h == _ph && _petalBmp != null) return;
            _pw = w; _ph = h;
            _petalBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _petalPix = new int[w * h];
            PetalLayer.Source = _petalBmp;
        }

        private void NewSpark(ref Spark p)
        {
            p.X = _rng.NextDouble() * _pw;
            p.Y = _ph + 1 + _rng.NextDouble() * 3;
            p.Vx = (_rng.NextDouble() - 0.5) * 0.12;
            p.Vy = -(0.15 + _rng.NextDouble() * 0.35);        // up
            p.Phase = _rng.NextDouble() * Math.PI * 2;
            p.Wobble = 0.2 + _rng.NextDouble() * 0.6;
            // most burn out partway up; a few make it to the top
            double reach = _ph * (0.35 + _rng.NextDouble() * 0.75);
            p.Life = reach / -p.Vy;
            p.Age = 0;
            p.Tone = _rng.Next(SparkTones.Length);
        }

        private void StepPetals()
        {
            if (_petalBmp == null || !IsVisible) return;
            Array.Clear(_petalPix, 0, _petalPix.Length);
            for (int i = 0; i < PetalCount; i++)
            {
                Spark p = _sparks[i];
                p.Age++;
                p.X += p.Vx;
                p.Y += p.Vy;
                if (p.Y < -2 || p.Age > p.Life) { NewSpark(ref p); _sparks[i] = p; continue; }
                double sx = p.X + Math.Sin(p.Phase + p.Age * 0.12) * p.Wobble;
                double t = p.Age / p.Life;                     // 0 at the bottom, 1 burnt out
                double a = Math.Min(1, p.Age / 8.0) * (1 - t * t) * (0.65 + 0.35 * _rng.NextDouble());   // flicker
                int x = (int)Math.Round(sx), y = (int)Math.Round(p.Y);
                int c = t > 0.6 && p.Tone == 0 ? SparkTones[2] : SparkTones[p.Tone];  // cools to amber
                Plot(x, y, c, a * 0.95);
                Plot(x, y + 1, c, a * 0.35);                   // the trail below it
                if (p.Tone == 0) { Plot(x - 1, y, c, a * 0.18); Plot(x + 1, y, c, a * 0.18); }   // brighter ones glow
                _sparks[i] = p;
            }
            _petalBmp.WritePixels(new Int32Rect(0, 0, _pw, _ph), _petalPix, _pw * 4, 0);
        }

        private void Plot(int x, int y, int rgb, double a)
        {
            if (x < 0 || y < 0 || x >= _pw || y >= _ph || a <= 0) return;
            int alpha = (int)(Math.Min(1, a) * 255);
            int k = y * _pw + x;
            int old = (_petalPix[k] >> 24) & 0xFF;
            if (alpha <= old) return;
            // premultiplied BGRA
            int r = ((rgb >> 16) & 0xFF) * alpha / 255, g = ((rgb >> 8) & 0xFF) * alpha / 255, b = (rgb & 0xFF) * alpha / 255;
            _petalPix[k] = (alpha << 24) | (r << 16) | (g << 8) | b;
        }
    }
}
