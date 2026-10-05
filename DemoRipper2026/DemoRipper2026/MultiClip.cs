using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DemoRipper2026
{
    /// <summary>
    /// More than one clip from one demo: each extra clip has its own pair of
    /// sliders, the folder is named "Mycelium - MAP - 2k dsr noice ns 700 - 715 - 3k scar 810 - 825",
    /// and saving jumps to the first clip, records it, then jumps to the next and records that.
    /// Clip 1 is the window's own fields and ticks; clips 2+ live here.
    /// A chain needs both ticks on every clip: each one is jumped to, recorded from its start tick
    /// and stopped at its end tick, and the takes are saved together into one folder.
    /// </summary>
    public partial class MainWindow
    {
        private const int MaxClips = 5;

        private sealed class ExtraClip
        {
            public int Number;
            public Border Root;
            public TextBox Kills, Details;
            public TextBlock KillsHint, DetailsHint, RangeText, InText, OutText;
            public Slider StartSlider, EndSlider;
            public int MarkIn = -1, MarkOut = -1;
            public bool SettingFromMark;
            public bool Complete { get { return MarkIn >= 0 && MarkOut >= 0; } }
            public string Words
            {
                get
                {
                    var w = new List<string>();
                    foreach (string s in new[] { Kills.Text, Details.Text }) if (s.Trim().Length > 0) w.Add(s.Trim());
                    return string.Join(" ", w);
                }
            }
        }

        private readonly List<ExtraClip> _extras = new List<ExtraClip>();
        private int _chainIndex = -1;                       // -1: no chain; 0: clip 1; k: _extras[k-1]
        private readonly List<string> _chainTakes = new List<string>();
        private readonly List<string> _chainNotes = new List<string>();
        private bool _flashOn;

        private int ClipCount { get { return 1 + _extras.Count; } }

        // ---------- the clip being recorded in a chain (clip 1 otherwise) ----------
        private int CurIn() { return _chainIndex <= 0 ? In1() : InOf(_extras[_chainIndex - 1]); }
        private int CurOut() { return _chainIndex <= 0 ? Out1() : OutOf(_extras[_chainIndex - 1]); }

        // The Set start / Set end tick buttons stay hidden: a slider's time IS a tick
        // (the on-screen replay clock is tick - 1500 ms, TickSeconds). Numpad * and - still set an
        // exact tick (and move the slider); moving the slider by hand goes back to the slider's time.
        private static int SliderTick(Slider s) { return (int)s.Value * 1000 + TickClockOffsetMs; }
        private bool Clip1Ranged { get { return EndSlider.Value > StartSlider.Value; } }
        private int In1() { return _markIn >= 0 ? _markIn : Clip1Ranged ? SliderTick(StartSlider) : -1; }
        private int Out1() { return _markOut >= 0 ? _markOut : Clip1Ranged ? SliderTick(EndSlider) : -1; }
        private static bool Ranged(ExtraClip c) { return c.EndSlider.Value > c.StartSlider.Value || (c.MarkIn >= 0 && c.MarkOut > c.MarkIn); }
        private static int InOf(ExtraClip c) { return c.MarkIn >= 0 ? c.MarkIn : Ranged(c) ? SliderTick(c.StartSlider) : -1; }
        private static int OutOf(ExtraClip c) { return c.MarkOut >= 0 ? c.MarkOut : Ranged(c) ? SliderTick(c.EndSlider) : -1; }

        private bool AllClipsMarked
        {
            get { return In1() >= 0 && Out1() >= 0 && _extras.All(c => InOf(c) >= 0 && OutOf(c) >= 0); }
        }

        private static string TickLabel(int exact, int derived)
        {
            if (exact >= 0) return FormatTime(TickSeconds(exact)) + "  (tick " + exact + ")";
            return derived >= 0 ? FormatTime(TickSeconds(derived)) + "  (from the slider)" : "not set";
        }

        /// <summary>" - 3k scar 810 - 825" for every extra clip, in order.</summary>
        private string ExtraNameParts()
        {
            var sb = new StringBuilder();
            foreach (ExtraClip c in _extras)
            {
                string w = c.Words;
                sb.Append(" - ").Append((w.Length > 0 ? w + " " : "") + FolderTime((int)c.StartSlider.Value) + " - " + FolderTime((int)c.EndSlider.Value));
            }
            return sb.ToString();
        }

        // ---------- UI ----------

        private void AddClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClipCount >= MaxClips) return;
            var c = new ExtraClip { Number = ClipCount + 1 };

            var root = new StackPanel();
            var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var w in new[] { GridLength.Auto, new GridLength(8), new GridLength(56), new GridLength(6),
                                      new GridLength(1, GridUnitType.Star), new GridLength(6), GridLength.Auto })
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var title = new TextBlock { Text = "Clip " + c.Number, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            c.Kills = new TextBox(); c.Details = new TextBox();
            c.KillsHint = Hint("kills"); c.DetailsHint = Hint("gun, notable animation");
            var remove = new Button { Content = "✕", Padding = new Thickness(8, 1, 8, 1), FontSize = 11, ToolTip = "Remove clip " + c.Number };
            Put(head, title, 0); Put(head, c.Kills, 2); Put(head, c.KillsHint, 2); Put(head, c.Details, 4); Put(head, c.DetailsHint, 4); Put(head, remove, 6);
            root.Children.Add(head);

            c.StartSlider = new Slider { Minimum = 0, Maximum = MaxSeconds, SmallChange = 1, LargeChange = 15, Margin = new Thickness(0, 2, 0, 0) };
            c.EndSlider = new Slider { Minimum = 0, Maximum = MaxSeconds, SmallChange = 1, LargeChange = 15, Margin = new Thickness(0, 2, 0, 2) };
            int from = Math.Max((int)EndSlider.Value, _extras.Count > 0 ? (int)_extras[_extras.Count - 1].EndSlider.Value : 0);
            c.StartSlider.Value = from; c.EndSlider.Value = from;
            c.RangeText = new TextBlock { FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center };
            root.Children.Add(c.StartSlider); root.Children.Add(c.EndSlider); root.Children.Add(c.RangeText);

            var marks = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            marks.ColumnDefinitions.Add(new ColumnDefinition()); marks.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            c.InText = new TextBlock { Text = "not set", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(c.InText);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            c.OutText = new TextBlock { Text = "not set", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(c.OutText);
            Grid.SetColumn(left, 0); Grid.SetColumn(right, 1);
            marks.Children.Add(left); marks.Children.Add(right);
            root.Children.Add(marks);

            c.Root = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x1E, 0x12)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x3D, 0x1A)),
                BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), Child = root
            };

            c.Kills.TextChanged += (s, a) => ExtraChanged(c);
            c.Details.TextChanged += (s, a) => ExtraChanged(c);
            c.StartSlider.ValueChanged += (s, a) =>
            {
                if (c.EndSlider.Value < c.StartSlider.Value) c.EndSlider.Value = c.StartSlider.Value;
                if (!c.SettingFromMark && c.MarkIn >= 0 && Clamp(TickSeconds(c.MarkIn)) != (int)c.StartSlider.Value) c.MarkIn = -1;
                ExtraChanged(c);
            };
            c.EndSlider.ValueChanged += (s, a) =>
            {
                if (c.EndSlider.Value < c.StartSlider.Value) c.EndSlider.Value = c.StartSlider.Value;
                if (!c.SettingFromMark && c.MarkOut >= 0 && Clamp(TickSeconds(c.MarkOut)) != (int)c.EndSlider.Value) c.MarkOut = -1;
                ExtraChanged(c);
            };
            remove.Click += (s, a) => RemoveExtra(c);

            _extras.Add(c);
            ExtraClipsPanel.Children.Add(c.Root);
            ExtraChanged(c);
            StatusText.Text = "Clip " + c.Number + " added. Drag its sliders to its start and end (numpad * and - set them exactly from the replay).";
        }

        private static TextBlock Hint(string text)
        {
            return new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x7B, 0x58)),
                                   IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        }

        private static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

        private void RemoveExtra(ExtraClip c)
        {
            if (_chainIndex >= 0 || Recording || _armed || _seeking) { Refuse("Not while the clips are recording."); return; }
            _extras.Remove(c);
            ExtraClipsPanel.Children.Remove(c.Root);
            for (int i = 0; i < _extras.Count; i++)
            {
                _extras[i].Number = i + 2;
                var head = (Grid)((StackPanel)_extras[i].Root.Child).Children[0];
                ((TextBlock)head.Children[0]).Text = "Clip " + _extras[i].Number;
            }
            UpdateNamePreview();
            UpdateMultiUi();
        }

        private void ExtraChanged(ExtraClip c)
        {
            c.KillsHint.Visibility = c.Kills.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            c.DetailsHint.Visibility = c.Details.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            c.RangeText.Text = FormatTime((int)c.StartSlider.Value) + "  →  " + FormatTime((int)c.EndSlider.Value);
            c.InText.Text = "start  " + TickLabel(c.MarkIn, InOf(c));
            c.OutText.Text = "end  " + TickLabel(c.MarkOut, OutOf(c));
            UpdateNamePreview();
            UpdateMultiUi();
        }

        /// <summary>A tick for an extra clip: the live replay tick, on the same demo as clip 1.</summary>
        private void SetExtraMark(ExtraClip c, bool isIn)
        {
            int tick, end;
            if (!TryReadTick(out tick, out end)) { Refuse("No replay tick to mark. Is a demo playing in BO2?"); return; }
            string demo = Mem.ReadAscii(Mem.At(OffFileName), 64);
            if (_markDemo.Length > 0 && demo != _markDemo)
            { Refuse("Clip " + c.Number + "'s ticks must be on the same demo as clip 1."); return; }

            string note = "";
            if (isIn)
            {
                c.MarkIn = tick;
                if (c.MarkOut >= 0 && c.MarkOut < tick) { c.MarkOut = -1; note = " End tick cleared: it was before this start tick."; }
            }
            else
            {
                c.MarkOut = tick;
                if (c.MarkIn >= 0 && c.MarkIn > tick) { c.MarkIn = -1; note = " Start tick cleared: it was after this end tick."; }
            }
            c.SettingFromMark = true;
            try
            {
                if (c.MarkIn >= 0) c.StartSlider.Value = Clamp(TickSeconds(c.MarkIn));
                if (c.MarkOut >= 0) { if (c.EndSlider.Value < c.StartSlider.Value) c.EndSlider.Value = c.StartSlider.Value; c.EndSlider.Value = Clamp(TickSeconds(c.MarkOut)); }
            }
            finally { c.SettingFromMark = false; }
            ExtraChanged(c);
            StatusText.Text = "Clip " + c.Number + ": " + (isIn ? "start" : "end") + " tick set at " + FormatTime(TickSeconds(tick))
                              + " (tick " + tick + ")." + note;
            MarkBeep(isIn ? MarkFeedback.In : MarkFeedback.Out);
        }

        /// <summary>Numpad * and -: the newest extra clip once there is one, clip 1 before that.</summary>
        private void SetMarkFromHotkey(bool isIn)
        {
            if (_extras.Count > 0) SetExtraMark(_extras[_extras.Count - 1], isIn);
            else SetMark(isIn);
        }

        private void ClearExtraMarks()
        {
            foreach (ExtraClip c in _extras) { c.MarkIn = -1; c.MarkOut = -1; ExtraChanged(c); }
        }

        /// <summary>The + and the green pulse on the button that records and saves everything.</summary>
        private void UpdateMultiUi()
        {
            if (AddClipButton == null || RecordButton == null) return;
            bool busy = Recording || _armed || _seeking || _recBusy || _saving || _chainIndex >= 0;
            bool lastDone = _extras.Count == 0 || (InOf(_extras[_extras.Count - 1]) >= 0 && OutOf(_extras[_extras.Count - 1]) >= 0);
            // Always visible; enabled once the last clip has a start and an end.
            bool canQueue = HasClipName && In1() >= 0 && Out1() >= 0 && lastDone && ClipCount < MaxClips && !busy;
            AddClipButton.Visibility = Visibility.Visible;
            AddClipButton.IsEnabled = canQueue;
            AddClipButton.Content = ClipCount >= MaxClips ? "Queue is full (" + MaxClips + " clips)"
                : "+  Queue for Recording   (clip " + (ClipCount + 1) + ")";
            ToolTipService.SetShowOnDisabled(AddClipButton, true);
            // The tooltip says what to do first to make it active: every step, ticked when done, so the
            // way to the button is visible.
            if (canQueue)
                AddClipButton.ToolTip = "Queue clip " + (ClipCount + 1) + " from this demo. FISH records every queued clip in turn and saves them in one folder.";
            else if (busy)
                AddClipButton.ToolTip = "Wait until the recording has finished.";
            else if (ClipCount >= MaxClips)
                AddClipButton.ToolTip = "Up to " + MaxClips + " clips per replay. Remove one with its ✕ to queue another.";
            else
            {
                var steps = new List<string> { "To queue another clip from this replay:" };
                steps.Add((HasClipName ? "  ✓  " : "  ✗  ") + "Name clip 1: type its kills or details (e.g. 2k, dsr noscope).");
                steps.Add((In1() >= 0 && Out1() >= 0 ? "  ✓  " : "  ✗  ")
                          + "Set clip 1's start and end: drag its two sliders apart, or press numpad * and - while the replay plays.");
                for (int i = 0; i < _extras.Count; i++)
                    steps.Add((InOf(_extras[i]) >= 0 && OutOf(_extras[i]) >= 0 ? "  ✓  " : "  ✗  ")
                              + "Set clip " + _extras[i].Number + "'s start and end: drag its two sliders apart.");
                AddClipButton.ToolTip = string.Join("\n", steps);
            }

            bool takeWaiting = !Recording && _takePath.Length > 0;
            bool ready = !busy && HasClipName && _library.Length > 0
                         && (takeWaiting || (AllClipsMarked && Mem.ModuleBase != IntPtr.Zero));
            SetFlash(ready);
        }

        private void SetFlash(bool on)
        {
            if (on == _flashOn) return;
            _flashOn = on;
            if (on)
            {
                var brush = new SolidColorBrush(Color.FromRgb(0x6B, 0x4A, 0x22));
                RecordButton.Background = brush;
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
                    Color.FromRgb(0x5C, 0x40, 0x1E), Color.FromRgb(0x2E, 0x9E, 0x48), TimeSpan.FromMilliseconds(700))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                RecordButton.ToolTip = ClipCount > 1
                    ? "Everything is in place: records clips 1-" + ClipCount + " in turn and saves them in one folder (numpad /)."
                    : "Everything is in place: records the clip and rips the demo into its folder (numpad /).";
            }
            else
            {
                RecordButton.ClearValue(BackgroundProperty);
                RecordButton.ToolTip = "FISH: records the clip(s) and rips the demo into its folder (numpad /).";
            }
        }

        // ---------- the chain ----------

        /// <summary>Called by BeginTake before anything is captured. False (with a status) refuses the take.</summary>
        private bool StartChainIfAny()
        {
            _chainTakes.Clear();
            _chainNotes.Clear();
            if (_extras.Count == 0) { _chainIndex = -1; return true; }
            if (!AllClipsMarked) { Refuse("Give every clip a start and an end (clip 1 and clips 2-" + ClipCount + ")."); return false; }
            _chainIndex = 0;
            return true;
        }

        /// <summary>A chain take ended with a file: on to the next clip, or save them all.</summary>
        private bool ChainTookTake(string path)
        {
            if (_chainIndex < 0) return false;
            if (path == null) { AbortChain("Clip " + (_chainIndex + 1) + " didn't finish its file."); return false; }
            _chainTakes.Add(path);
            _chainNotes.Add(_takeNote);
            if (_chainIndex < _extras.Count)
            {
                _chainIndex++;
                NextChainTake();
                return true;
            }
            _chainIndex = -1;                                // all recorded: save as one
            _takePath = _chainTakes[0];
            _takeNote = string.Join("\r\n", _chainNotes.Select((n, i) => "clip " + (i + 1) + ": " + n.Replace("\r\n", "; ")));
            SaveClip(true);
            return true;
        }

        private void NextChainTake()
        {
            IRecorder rec = _active;
            string takeBase = Path.Combine(_library, TakesFolder,
                "take " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + " clip " + (_chainIndex + 1));
            _takeBase = takeBase;
            _takePath = "";
            _takeNote = "";
            SetRecBusy(true, "Clip " + _chainIndex + " recorded. Getting clip " + (_chainIndex + 1) + " of " + ClipCount + " ready…");
            var t = new Thread(() =>
            {
                string error = null;
                try { rec.Prepare(takeBase); } catch (Exception ex) { error = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetRecBusy(false, null);
                    if (error != null) { AbortChain("Couldn't get clip " + (_chainIndex + 1) + " ready: " + error); return; }
                    JumpAndArm();
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AbortChain(string why)
        {
            int done = _chainTakes.Count;
            _chainIndex = -1;
            _capture = null;
            Refuse(why + (done > 0
                ? " The " + done + " clip(s) already recorded stay in " + Path.Combine(_library, TakesFolder) + "."
                : ""));
            _chainTakes.Clear();
            _chainNotes.Clear();
            UpdateRecordUi();
        }

        /// <summary>The chain's takes, each with the name of its own clip, for SaveClip.</summary>
        private List<KeyValuePair<string, string>> ChainFiles(string player, string mapToken)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < _chainTakes.Count; i++)
            {
                string words, s, e;
                if (i == 0)
                {
                    var w = new List<string>();
                    foreach (string x in new[] { KillsBox.Text, DetailsBox.Text }) if (x.Trim().Length > 0) w.Add(x.Trim());
                    words = string.Join(" ", w); s = FolderTime((int)StartSlider.Value); e = FolderTime((int)EndSlider.Value);
                }
                else
                {
                    ExtraClip c = _extras[i - 1];
                    words = c.Words; s = FolderTime((int)c.StartSlider.Value); e = FolderTime((int)c.EndSlider.Value);
                }
                var parts = new List<string>();
                foreach (string p in new[] { player, mapToken }) if (!string.IsNullOrEmpty(p) && p.Trim().Length > 0) parts.Add(p.Trim());
                parts.Add((words.Length > 0 ? words + " " : "") + s + " - " + e);
                list.Add(new KeyValuePair<string, string>(_chainTakes[i], Sanitise(string.Join(" - ", parts))));
            }
            return list;
        }

        /// <summary>clip.txt lines for clips 2+: part2=..., part2_start=..., ticks, preview file.</summary>
        private List<string> ExtraManifestLines(List<string> previews)
        {
            var lines = new List<string>();
            CultureInfo inv = CultureInfo.InvariantCulture;
            for (int i = 0; i < _extras.Count; i++)
            {
                ExtraClip c = _extras[i];
                string k = "part" + c.Number;
                lines.Add(k + "=" + c.Words);
                lines.Add(k + "_start=" + FormatTime((int)c.StartSlider.Value));
                lines.Add(k + "_end=" + FormatTime((int)c.EndSlider.Value));
                if (InOf(c) >= 0) lines.Add(k + "_start_tick=" + InOf(c).ToString(inv));
                if (OutOf(c) >= 0) lines.Add(k + "_end_tick=" + OutOf(c).ToString(inv));
                if (previews != null && i + 1 < previews.Count && !string.IsNullOrEmpty(previews[i + 1])) lines.Add(k + "_preview=" + previews[i + 1]);
            }
            if (_extras.Count > 0) lines.Insert(0, "clips=" + ClipCount.ToString(inv));
            return lines;
        }
    }
}
