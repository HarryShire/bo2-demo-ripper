using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DemoRipper2026
{
    /// <summary>
    /// The demo time bar and the progress bar during recording. The time bar shows
    /// the playhead and, as a hatched fill, the part of the replay with no jump points yet (play over it
    /// to lay them). It is a display only, not a control. The progress bar fills during recording.
    ///
    /// BO2 lays a jump point every 10 s (tick 2000 + 10000n, Theater.cs) as a replay is first played,
    /// so the laid ones run up to the furthest tick played. DemoRipper can only know what it has seen
    /// play: the furthest tick it has read, per demo, while it was open.
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<string, int> _furthestSeen = new Dictionary<string, int>();
        private int _barTick = -1, _barEnd = -1;
        private string _barDemo = "";

        private static readonly Brush BarTrack = Frozen(Color.FromRgb(0x24, 0x1E, 0x12));
        private static readonly Brush BarKnown = Frozen(Color.FromRgb(0x3A, 0x2F, 0x1A));
        private static readonly Brush BarNotch = Frozen(Color.FromArgb(0xB0, 0xC9, 0xA4, 0x5C));
        private static readonly Brush BarClip = Frozen(Color.FromArgb(0x90, 0xC9, 0xA4, 0x5C));
        private static readonly Brush BarHead = Frozen(Color.FromRgb(0xF3, 0xE6, 0xC4));
        private static readonly Brush BarUnknownText = Frozen(Color.FromRgb(0x8C, 0x7B, 0x58));

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private static Brush UnknownFill()
        {
            // diagonal hatching: "not known yet"
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Frozen(Color.FromRgb(0x1C, 0x17, 0x0E)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
            g.Children.Add(new GeometryDrawing(null, new Pen(Frozen(Color.FromRgb(0x4A, 0x3A, 0x1E)), 1.2),
                new LineGeometry(new Point(0, 8), new Point(8, 0))));
            var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
            b.Freeze();
            return b;
        }
        private static readonly Brush BarUnknown = UnknownFill();

        /// <summary>From the poll: the replay's tick and end (ok = a replay is playing).</summary>
        private void UpdateDemoBar(bool ok, int tick, int end)
        {
            if (DemoBar == null) return;
            if (ok)
            {
                string demo = Mem.ReadAscii(Mem.At(OffFileName), 64);
                if (demo.Length > 0)
                {
                    int seen;
                    _furthestSeen.TryGetValue(demo, out seen);
                    if (tick > seen) _furthestSeen[demo] = tick;
                    _barDemo = demo;
                }
                _barTick = tick; _barEnd = end;
                FitSlidersToDemo(end);
            }
            else { _barTick = -1; }
            DrawDemoBar();
            UpdateRecordProgress(ok, tick);
        }

        /// <summary>The sliders span the demo's real length, so they line up with the bar.</summary>
        private void FitSlidersToDemo(int end)
        {
            int max = Math.Max(1, Math.Min(MaxSeconds, TickSeconds(end) + 1));
            if ((int)StartSlider.Maximum == max) return;
            foreach (Slider s in AllSliders()) if (s.Value <= max) s.Maximum = max;
        }

        private IEnumerable<Slider> AllSliders()
        {
            yield return StartSlider; yield return EndSlider;
            foreach (ExtraClip c in _extras) { yield return c.StartSlider; yield return c.EndSlider; }
        }

        private void DrawDemoBar()
        {
            DemoBar.Children.Clear();
            DemoBarTop.Children.Clear();
            DemoBarTime.Children.Clear();
            double w = DemoBar.ActualWidth, h = DemoBar.ActualHeight;
            if (w < 10 || h < 4) return;
            Add(new Rectangle { Width = w, Height = h, Fill = BarTrack, RadiusX = 3, RadiusY = 3 }, 0, 0);
            DemoBarUnknown.Height = h;
            if (_barEnd <= 0)
            {
                DemoBarUnknown.Visibility = Visibility.Collapsed;
                var none = new TextBlock { Text = "no replay playing", FontSize = 10, Foreground = BarUnknownText };
                Add(none, 6, (h - 13) / 2);
                return;
            }
            Func<int, double> X = t => Math.Max(0, Math.Min(w, w * t / (double)_barEnd));

            int seen; _furthestSeen.TryGetValue(_barDemo, out seen);
            double known = X(seen);
            Add(new Rectangle { Width = known, Height = h, Fill = BarKnown }, 0, 0);

            // the jump points BO2 has laid: every 10 s, as far as it was played
            for (int t = 12000; t <= seen && t < _barEnd; t += 10000)
                Add(new Rectangle { Width = 1, Height = h * 0.45, Fill = BarNotch }, X(t), h * 0.55);

            // not laid yet: a hatched fill, no label
            if (known < w - 1)
            {
                DemoBarUnknown.Fill = BarUnknown;
                DemoBarUnknown.Margin = new Thickness(known, 0, 0, 0);
                DemoBarUnknown.Width = w - known;
                DemoBarUnknown.Visibility = Visibility.Visible;
            }
            else DemoBarUnknown.Visibility = Visibility.Collapsed;

            // the clips: clip 1 and every queued one
            DrawClipSpan(X, In1(), Out1(), h);
            foreach (ExtraClip c in _extras) DrawClipSpan(X, InOf(c), OutOf(c), h);

            if (_barTick > 0)   // the playhead goes on the top layer, over the hatching
            {
                double x = X(_barTick);
                // the current time, above the playhead
                var time = new TextBlock
                {
                    Text = FormatTime(TickSeconds(_barTick)), FontSize = 14, FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Bahnschrift, Segoe UI"), FontStretch = FontStretches.Condensed,
                    Foreground = BarHead, IsHitTestVisible = false
                };
                time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double tw = time.DesiredSize.Width;
                Canvas.SetLeft(time, Math.Max(0, Math.Min(w - tw, x - tw / 2)));
                Canvas.SetTop(time, Math.Max(0, DemoBarTime.ActualHeight - time.DesiredSize.Height + 2));
                DemoBarTime.Children.Add(time);
                AddTop(new Rectangle { Width = 2, Height = h, Fill = BarHead }, x - 1, 0);
                AddTop(new Polygon { Points = new PointCollection { new Point(-4, 0), new Point(4, 0), new Point(0, 5) }, Fill = BarHead }, x, 0);
            }
        }

        private void DrawClipSpan(Func<int, double> X, int a, int b, double h)
        {
            if (a < 0 || b <= a) return;
            double x0 = X(a), x1 = X(b);
            Add(new Rectangle { Width = Math.Max(2, x1 - x0), Height = 4, Fill = BarClip, IsHitTestVisible = false }, x0, 0);
        }

        private void Add(UIElement e, double x, double y)
        {
            e.IsHitTestVisible = false;
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            DemoBar.Children.Add(e);
        }

        private void AddTop(UIElement e, double x, double y)
        {
            e.IsHitTestVisible = false;
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            DemoBarTop.Children.Add(e);
        }

        /// <summary>
        /// While a take records: how far between its start and end the replay is, across every queued
        /// clip in a chain. Armed or jumping in a chain: the clips done so far. The demo capture still
        /// drives it while it runs (busy).
        /// </summary>
        private void UpdateRecordProgress(bool ok, int tick)
        {
            if (RipProgress == null || _recBusy || _saving) return;
            int n = ClipCount, done = _chainIndex >= 0 ? _chainIndex : 0;
            double whole;
            if (Recording && ok)
            {
                int a = CurIn(), b = CurOut();
                double frac = b > a ? Math.Max(0, Math.Min(1, (tick - a) / (double)(b - a))) : 0;
                whole = _chainIndex >= 0 ? (done + frac) / n : frac;
            }
            else if (_chainIndex >= 0) whole = done / (double)n;
            else whole = 0;
            RipProgress.Value = whole * 100;
            RipProgress.ToolTip = Recording ? "Recording: how far through the clip" + (_chainIndex >= 0 ? "s" : "") + " the replay is."
                                            : "Fills while a demo is read from BO2 and while clips record.";
        }
    }
}
