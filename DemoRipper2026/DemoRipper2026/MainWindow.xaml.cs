using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DemoRipper2026
{
    public partial class MainWindow : Window
    {
        // Retail build 2026-08-17: the demo's data (pointer, then size), its file name, thumbnail,
        // summary and the summary's length.
        private const int DefDemoData = 47344080;
        private const int DefFileName = 44940850;
        private const int DefThumb = 44941368;
        private const int DefSummary = 44589248;
        private const int DefSummaryLength = 44941884;

        private const int ZeroRunWarn = 50;
        private const int MaxDemoBytes = 256 * 1024 * 1024;
        private const int MaxSeconds = 720;   // no replay runs past 12 minutes
        private const int DefObsPort = 4455;  // obs-websocket's default

        // Replay clock, retail build 2026-08-17: the current tick and the replay's last tick.
        private const int DefTick = 13068896;
        private const int DefEndTick = 46225264;

        // The on-screen replay timer shows whole seconds: floor((tick - 1500) / 1000).
        private const int TickClockOffsetMs = 1500;

        private static readonly string SettingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "DemoRipper.settings.txt");

        private int OffDemoData { get { return ParseBox(OffDemoDataBox, DefDemoData); } }
        private int OffFileName { get { return ParseBox(OffFileNameBox, DefFileName); } }
        private int OffThumb { get { return ParseBox(OffThumbBox, DefThumb); } }
        private int OffSummary { get { return ParseBox(OffSummaryBox, DefSummary); } }
        private int OffSummaryLength { get { return ParseBox(OffSummaryLengthBox, DefSummaryLength); } }
        private int OffTick { get { return ParseBox(OffTickBox, DefTick); } }
        private int OffEndTick { get { return ParseBox(OffEndTickBox, DefEndTick); } }

        private static int ParseBox(TextBox box, int fallback)
        {
            int v;
            if (box != null && int.TryParse(box.Text.Trim(), out v)) return v;
            return fallback;
        }

        private readonly DispatcherTimer _poll;
        private bool _syncing;          // guards slider <-> textbox feedback
        private bool _ready;

        private string _library = "";
        private string _lastFolder = "";

        private ObsRecorder _obs;
        private BuiltInRecorder _builtIn;
        private IRecorder _active;               // the recorder the current take uses
        private string _takeBase = "";           // where the current take records, without extension
        private bool _recBusy;
        private readonly Stopwatch _recClock = new Stopwatch();

        // A take is the demo captured when Record was pressed plus the preview
        // that was recorded. Nothing goes into a clip folder until both exist, so the
        // folder is named once, at save time, from the name fields as they are then.
        private const string TakesFolder = "_takes";
        private DemoCapture _capture;            // null until Record or Rip Demo
        private string _takePath = "";           // finished preview, still in the takes folder
        private string _takeNote = "";           // tick timings for the save log
        private bool _saving;

        private string _lastSeenDemo = "";       // map dropdown follows this

        // Global hotkeys on the numpad: / record, * mark in, - mark out, + go to the start. Numpad
        // keys have their own virtual-key codes, so the main keyboard's / and -
        // still type normally. Registered system-wide so they work while BO2
        // has focus.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerhotkey
        // https://learn.microsoft.com/windows/win32/inputdev/virtual-key-codes
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;   // holding the key doesn't retrigger
        private const uint VK_MULTIPLY = 0x6A;      // numpad *
        private const uint VK_SUBTRACT = 0x6D;      // numpad -
        private const uint VK_DIVIDE = 0x6F;        // numpad /
        private const uint VK_ADD = 0x6B;           // numpad +
        private const int HotkeyRecord = 1;
        private const int HotkeyMarkIn = 2;
        private const int HotkeyMarkOut = 3;
        private const int HotkeyGoToStart = 4;

        // Jumping to the start tick with BO2's theater keys (Theater.cs). One seek at a time.
        private volatile bool _seeking;
        private volatile bool _cancelSeek;
        private bool _goToRunning;                 // the seek is "Go to", not part of a take
        // Key to pause takes about 22 ms on retail, so aim that far ahead; each
        // "Go to" nudges it from where the pause landed.
        private int _pauseLeadMs = 24;

        private HwndSource _hwnd;
        private readonly List<int> _hotkeys = new List<int>();   // ids that registered

        // In/out points as replay ticks, -1 when unset. They belong to one demo.
        private int _markIn = -1, _markOut = -1;
        private int _markEndTick;
        private string _markDemo = "";
        private bool _settingFromMark;

        // Tick watcher. Its own thread, because a frame-rate poll can't ride the
        // dispatcher (it gets starved). Values are snapshotted on the
        // UI thread before the watcher reads them.
        private volatile bool _armed;           // waiting for the start tick to start recording
        private volatile bool _autoEndActive;   // recording; stop when the end tick is crossed
        private volatile int _watchIn = -1, _watchOut = -1, _watchOffTick;

        public MainWindow()
        {
            InitializeComponent();
            PopulateMaps();
            StartRecordLabelFade();

            _poll = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _poll.Tick += (s, e) =>
            {
                UpdateAttach();
                CheckObsWhileArmed();
                UpdateRecordUi();
                UpdateTickUi();
                UpdateMapFromGame();
                UpdatePlayerFromGame();
                UpdateAutoKbpsText();
            };
            PlayerCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                                   new TextChangedEventHandler(PlayerText_Changed));
            _poll.Start();

            Thread watcher = new Thread(WatchTicks);
            watcher.IsBackground = true;
            watcher.Name = "Tick watcher";
            watcher.Start();

            LoadSettings();
            Loaded += (s, e) => StartPetals();
            _ready = true;

            SyncFromSliders();
            UpdatePlaceholder();
            UpdateLibraryUi();
            UpdateAttach();
            UpdateRecordUi();
            UpdateTickUi();
            UpdateMapFromGame();
            UpdateNamePreview();
        }

        // ================= CONNECTION =================

        private bool UpdateAttach()
        {
            bool ok = Mem.Attach(ProcessBox.Text.Trim()) && Mem.ModuleBase != IntPtr.Zero;

            if (ok)
            {
                ConnectionText.Text = "Found Retail BO2 successfully";
                ConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xC4, 0x6E));
            }
            else
            {
                ConnectionText.Text = "Not connected";
                ConnectionText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x89, 0x3A));
            }
            return ok;
        }

        // ================= TIMES =================

        /// <summary>Display form, matching BO2's own timer: 2:07</summary>
        private static string FormatTime(int seconds)
        {
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        /// <summary>Folder form — no colon, since Windows won't take one: 207</summary>
        private static string FolderTime(int seconds)
        {
            return (seconds / 60).ToString() + (seconds % 60).ToString("00");
        }

        /// <summary>
        /// Accepts "2:07", "207" and "34". Bare digits treat the last two as
        /// seconds, so 034 is 0:34 and 1005 is 10:05.
        /// </summary>
        private static bool ParseTime(string text, out int seconds)
        {
            seconds = 0;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;

            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                int m, s;
                if (!int.TryParse(text.Substring(0, colon).Trim(), out m)) return false;
                if (!int.TryParse(text.Substring(colon + 1).Trim(), out s)) return false;
                seconds = m * 60 + s;
                return true;
            }

            foreach (char c in text) if (c < '0' || c > '9') return false;

            if (text.Length <= 2) { seconds = int.Parse(text, CultureInfo.InvariantCulture); }
            else
            {
                int m = int.Parse(text.Substring(0, text.Length - 2), CultureInfo.InvariantCulture);
                int s = int.Parse(text.Substring(text.Length - 2), CultureInfo.InvariantCulture);
                seconds = m * 60 + s;
            }
            return true;
        }

        private static int Clamp(int v)
        {
            if (v < 0) return 0;
            return v > MaxSeconds ? MaxSeconds : v;
        }

        private void StartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _syncing) return;

            // The out point can never sit before the in point.
            if (EndSlider.Value < StartSlider.Value) EndSlider.Value = StartSlider.Value;
            SyncFromSliders();
            if (!_settingFromMark) ReconcileMarks();
        }

        private void EndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _syncing) return;

            if (EndSlider.Value < StartSlider.Value) EndSlider.Value = StartSlider.Value;
            SyncFromSliders();
            if (!_settingFromMark) ReconcileMarks();
        }

        private void SyncFromSliders()
        {
            _syncing = true;
            int a = (int)StartSlider.Value, b = (int)EndSlider.Value;
            StartBox.Text = FormatTime(a);
            EndBox.Text = FormatTime(b);
            RangeText.Text = FormatTime(a) + "  \u2192  " + FormatTime(b);
            _syncing = false;
            UpdateNamePreview();
        }

        private void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_ready || _syncing) return;

            int a, b;
            if (!ParseTime(StartBox.Text, out a)) return;
            if (!ParseTime(EndBox.Text, out b)) return;

            a = Clamp(a);
            b = Clamp(b);
            if (b < a) b = a;

            _syncing = true;
            StartSlider.Value = a;
            EndSlider.Value = b;
            RangeText.Text = FormatTime(a) + "  \u2192  " + FormatTime(b);
            _syncing = false;
            ReconcileMarks();
            UpdateNamePreview();
        }

        private void NameField_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholder();
            UpdateNamePreview();
            if (HasClipName) HideNameWarning();
        }

        private void UpdatePlaceholder()
        {
            if (KillsPlaceholder == null || DetailsPlaceholder == null) return;
            if (PlayerPlaceholder != null && PlayerCombo != null)
                PlayerPlaceholder.Visibility = string.IsNullOrEmpty(PlayerCombo.Text) ? Visibility.Visible : Visibility.Collapsed;
            KillsPlaceholder.Visibility = KillsBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            DetailsPlaceholder.Visibility = DetailsBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool HasClipName
        {
            get { return KillsBox.Text.Trim().Length > 0 || DetailsBox.Text.Trim().Length > 0; }
        }

        private Maps.Entry SelectedMap
        {
            get { return MapCombo.SelectedItem as Maps.Entry; }
        }

        /// <summary>"Mycelium - overflow - 3k scar cool jumpyy 120 - 128"</summary>
        private string BuildFolderName()
        {
            int a, b;
            ParseTime(StartBox.Text, out a);
            ParseTime(EndBox.Text, out b);
            Maps.Entry map = SelectedMap;
            return Sanitise(BuildFolderName(PlayerText, KillsBox.Text, map != null ? map.Token : "", DetailsBox.Text, Clamp(a), Clamp(b))
                            + ExtraNameParts());
        }

        /// <summary>
        /// [player] - [map] - [kills] [gun] [notable animation] start - end, blank parts skipped.
        /// The player is the in-game name of whoever is being watched.
        /// </summary>
        private static string BuildFolderName(string player, string kills, string mapToken, string details, int startSec, int endSec)
        {
            var words = new List<string>();
            foreach (string w in new[] { kills, details })
                if (!string.IsNullOrEmpty(w) && w.Trim().Length > 0) words.Add(w.Trim());
            var parts = new List<string>();
            foreach (string p in new[] { player, mapToken })
                if (!string.IsNullOrEmpty(p) && p.Trim().Length > 0) parts.Add(p.Trim());
            // One clip: "<kills gun detail> <start> - <end>" ("Mycelium - MAP - 2k dsr noice ns
            // 700 - 715 - 3k scar 810 - 825"), so further clips can follow the same way.
            string clip = string.Join(" ", words);
            parts.Add((clip.Length > 0 ? clip + " " : "") + FolderTime(startSec) + " - " + FolderTime(endSec));
            return string.Join(" - ", parts);
        }

        // ================= PLAYER NAME =================
        // The player being watched, read from the game (read-only), or typed. Retail t6mp, 2026-08-17
        // build: the players at module+0x2C8FBDC, 0x120 bytes each with the name first, indexed by the
        // theater's spectate slot; the slot being followed at module+0x256E440.
        private const int RosterStride = 0x120, RosterSlots = 18;
        private bool _settingPlayer;
        private string _rosterKey = "";

        private string PlayerText
        {
            get { return PlayerCombo == null ? "" : (PlayerCombo.Text ?? "").Trim(); }
        }

        private bool PlayerTables(out int roster, out int selected)
        {
            string p = (Mem.AttachedName ?? "").ToLowerInvariant();
            if (p == "t6mp") { roster = 0x2C8FBDC; selected = 0x256E440; return true; }
            roster = selected = -1;
            return false;
        }

        private static string ReadName(IntPtr at)
        {
            byte[] b = Mem.ReadBytes(at, 40);
            int n = Array.IndexOf(b, (byte)0);
            if (n <= 0) return "";
            string s;
            try { s = new UTF8Encoding(false, true).GetString(b, 0, n); } catch { return ""; }
            foreach (char c in s) if (char.IsControl(c)) return "";
            return s.Trim();
        }

        private void UpdatePlayerFromGame()
        {
            int roster, selected;
            if (Mem.ModuleBase == IntPtr.Zero || !PlayerTables(out roster, out selected)) return;

            var names = new string[RosterSlots];
            for (int i = 0; i < RosterSlots; i++) names[i] = ReadName(Mem.At(roster + i * RosterStride));

            string key = string.Join("\n", names);
            if (key != _rosterKey)
            {
                _rosterKey = key;
                string keep = PlayerCombo.Text;
                _settingPlayer = true;
                PlayerCombo.Items.Clear();
                foreach (string n in names) if (n.Length > 0 && !PlayerCombo.Items.Contains(n)) PlayerCombo.Items.Add(n);
                PlayerCombo.Text = keep;
                _settingPlayer = false;
            }

            if (AutoPlayerBox.IsChecked != true) return;
            int slot = Mem.ReadInt(Mem.At(selected));
            if (slot < 0 || slot >= RosterSlots || names[slot].Length == 0) return;
            if (PlayerCombo.Text != names[slot])
            {
                _settingPlayer = true;
                PlayerCombo.Text = names[slot];
                _settingPlayer = false;
                UpdatePlaceholder();
                UpdateNamePreview();
            }
        }

        /// <summary>Typing a name is a manual choice: auto stops following the game.</summary>
        private void PlayerText_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_settingPlayer && AutoPlayerBox.IsChecked == true && PlayerCombo.IsKeyboardFocusWithin)
                AutoPlayerBox.IsChecked = false;
            UpdatePlaceholder();
            UpdateNamePreview();
        }

        private void PlayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingPlayer || PlayerCombo.SelectedItem == null) return;
            // Only a real pick: the list open, or the user in the box. WPF can also change the selection a
            // moment after auto sets the text (it matches an item); that must not switch auto off.
            if (!PlayerCombo.IsDropDownOpen && !PlayerCombo.IsKeyboardFocusWithin) return;
            AutoPlayerBox.IsChecked = false;          // picked from the list by hand
            Dispatcher.BeginInvoke(new Action(() => { UpdatePlaceholder(); UpdateNamePreview(); }));
        }

        private void AutoPlayerBox_Click(object sender, RoutedEventArgs e)
        {
            int roster, selected;
            if (AutoPlayerBox.IsChecked == true && Mem.ModuleBase != IntPtr.Zero && !PlayerTables(out roster, out selected))
                StatusText.Text = "The player can only be read from retail BO2: pick or type the player.";
            _rosterKey = "";
            UpdatePlayerFromGame();
        }

        private void UpdateNamePreview()
        {
            if (NamePreviewText == null || KillsBox == null || MapCombo == null) return;
            NamePreviewText.Text = HasClipName ? "saves as   " + BuildFolderName() : "";
            UpdateMultiUi();
        }

        /// <summary>Orange "Name the clip first" over the name fields, pulsing gently until a name is typed.</summary>
        private void ShowNameWarning()
        {
            NameWarningText.Visibility = Visibility.Visible;
            NameWarningText.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.35, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        private void HideNameWarning()
        {
            NameWarningText.BeginAnimation(OpacityProperty, null);
            NameWarningText.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// The idle FISH label cross-fades between two copies of itself (RecordLabelA/B).
        /// Each holds 2.2 s, then they cross-fade over 0.4 s.
        /// </summary>
        private void StartRecordLabelFade()
        {
            RecordLabelA.BeginAnimation(OpacityProperty, LabelFade(1, 0));
            RecordLabelB.BeginAnimation(OpacityProperty, LabelFade(0, 1));
        }

        private static DoubleAnimationUsingKeyFrames LabelFade(double first, double second)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var a = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(5.2),
                RepeatBehavior = RepeatBehavior.Forever
            };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(first, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(first, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(second, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6)), ease));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(second, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.8))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(first, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.2)), ease));
            return a;
        }

        // ================= MAP =================

        private void PopulateMaps()
        {
            MapCombo.Items.Add(Maps.LeaveOut);
            foreach (Maps.Entry m in Maps.ByDisplayName()) MapCombo.Items.Add(m);
            MapCombo.SelectedItem = Maps.LeaveOut;
        }

        private void MapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateNamePreview();
        }

        /// <summary>
        /// Selects the map of the demo loaded in the game whenever that demo
        /// changes. A map picked by hand stays until a different demo loads.
        /// </summary>
        private void UpdateMapFromGame()
        {
            if (Mem.ModuleBase == IntPtr.Zero) return;
            string demo = Mem.ReadAscii(Mem.At(OffFileName), 64);
            if (demo.Length == 0 || demo == _lastSeenDemo) return;

            _lastSeenDemo = demo;
            SelectMap(Maps.FromFileName(demo));
        }

        private void SelectMap(Maps.Entry map)
        {
            if (map != null) MapCombo.SelectedItem = map;
        }

        private static string Sanitise(string name)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in name.Trim())
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '-' : c);
            return sb.ToString().Trim();
        }

        // ================= IN / OUT MARKS =================

        /// <summary>Tick to the seconds the on-screen replay timer shows.</summary>
        private static int TickSeconds(int tick)
        {
            return Math.Max(0, (tick - TickClockOffsetMs) / 1000);
        }

        /// <summary>The replay tick and the demo's end tick; false outside a playing replay.</summary>
        private bool TryReadTick(out int tick, out int endTick)
        {
            tick = 0;
            endTick = 0;
            if (!Mem.Attach(ProcessBox.Text.Trim()) || Mem.ModuleBase == IntPtr.Zero) return false;

            tick = Mem.ReadInt(Mem.At(OffTick));
            endTick = Mem.ReadInt(Mem.At(OffEndTick));

            // Playback stops a little short of the end tick, so a tick well
            // past it means these aren't replay values.
            return endTick > 0 && tick > 0 && tick <= endTick + 1000;
        }

        private void MarkInButton_Click(object sender, RoutedEventArgs e)
        {
            SetMark(true);
        }

        private void MarkOutButton_Click(object sender, RoutedEventArgs e)
        {
            SetMark(false);
        }

        /// <summary>
        /// Captures the live tick as the in or out point and moves the matching
        /// slider to it. Hotkeys call this from inside the game, so the beep is
        /// the only feedback the user gets there.
        /// </summary>
        private void SetMark(bool isIn)
        {
            int tick, end;
            if (!TryReadTick(out tick, out end))
            {
                StatusText.Text = "No replay tick to mark. Is a demo playing in retail BO2?";
                MarkBeep(MarkFeedback.Failed);
                return;
            }

            string demo = Mem.ReadAscii(Mem.At(OffFileName), 64);
            if (demo != _markDemo) { _markIn = -1; _markOut = -1; _markDemo = demo; ClearExtraMarks(); }
            _markEndTick = end;

            string note = "";
            if (isIn)
            {
                _markIn = tick;
                if (_markOut >= 0 && _markOut < tick) { _markOut = -1; note = " End tick cleared: it was before this start tick."; }
            }
            else
            {
                _markOut = tick;
                if (_markIn >= 0 && _markIn > tick) { _markIn = -1; note = " Start tick cleared: it was after this end tick."; }
            }

            ApplyMarksToTimes();
            StatusText.Text = (isIn ? "Start" : "End") + " time set to the current demo time, " + FormatTime(TickSeconds(tick))
                              + " (tick " + tick + ")." + note;
            MarkBeep(isIn ? MarkFeedback.In : MarkFeedback.Out);
            UpdateMarkText();
            RefreshWatch();
        }

        private void ClearMarks()
        {
            _markIn = -1;
            _markOut = -1;
            _markEndTick = 0;
            _markDemo = "";
            ClearExtraMarks();
            UpdateMarkText();
            RefreshWatch();
        }

        private void ApplyMarksToTimes()
        {
            _settingFromMark = true;
            try
            {
                if (_markIn >= 0) StartSlider.Value = Clamp(TickSeconds(_markIn));
                if (_markOut >= 0) EndSlider.Value = Clamp(TickSeconds(_markOut));
            }
            finally { _settingFromMark = false; }
        }

        /// <summary>
        /// A time edited by hand no longer matches its mark, so the mark is
        /// dropped rather than saved as a tick for a moment the user moved away from.
        /// </summary>
        private void ReconcileMarks()
        {
            if (_markIn >= 0 && Clamp(TickSeconds(_markIn)) != (int)StartSlider.Value) _markIn = -1;
            if (_markOut >= 0 && Clamp(TickSeconds(_markOut)) != (int)EndSlider.Value) _markOut = -1;
            UpdateMarkText();
            RefreshWatch();
        }

        private void UpdateTickUi()
        {
            int tick, end;
            bool replay = TryReadTick(out tick, out end);
            TickText.Text = replay
                ? "replay  " + FormatTime(TickSeconds(tick)) + " / " + FormatTime(TickSeconds(end))
                  + "     tick " + tick + " / " + end
                : "replay tick unavailable. Play a demo in retail BO2.";
            UpdateMarkText();
            UpdateDemoBar(replay, tick, end);
        }

        private void DemoBar_SizeChanged(object sender, SizeChangedEventArgs e) { DrawDemoBar(); }

        private void UpdateMarkText()
        {
            if (MarkInText == null) return;
            MarkInText.Text = "start  " + TickLabel(_markIn, In1());
            MarkOutText.Text = "end  " + TickLabel(_markOut, Out1());
            UpdateMultiUi();
        }

        private enum MarkFeedback { In, Out, Armed, Saved, Failed }

        private void MarkBeep(MarkFeedback kind)
        {
            if (BeepBox.IsChecked != true) return;
            // OBS would put the beep in the preview's audio. The built-in recorder only hears BO2.
            if (_active is ObsRecorder && _active.IsRecording) return;

            int freq = kind == MarkFeedback.In ? 1200 : kind == MarkFeedback.Out ? 800
                     : kind == MarkFeedback.Armed ? 1000 : kind == MarkFeedback.Saved ? 1600 : 300;
            int count = kind == MarkFeedback.Failed || kind == MarkFeedback.Armed ? 2 : 1;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { for (int i = 0; i < count; i++) Console.Beep(freq, 90); }
                catch { }
            });
        }

        /// <summary>
        /// clip.txt — the clip's manifest, for any tool that picks the clip up later.
        /// One key=value per line; lines with nothing to say are left out.
        /// </summary>
        private static string[] BuildManifest(string clip, string demoFile, string map, string mode,
                                              int size, DateTime ripped, int startSec, int endSec,
                                              int inTick, int outTick, int replayEndTick, string preview,
                                              string player = null)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "clip=" + clip,
                "player=" + (player ?? ""),
                "demo=" + demoFile,
                "map=" + map,
                "mode=" + mode,
                "size=" + size.ToString(inv),
                "ripped=" + ripped.ToString("yyyy-MM-dd HH:mm", inv),
                "source=retail t6mp",
                "start=" + FormatTime(startSec),
                "end=" + FormatTime(endSec)
            };
            if (inTick >= 0) lines.Add("start_tick=" + inTick.ToString(inv));
            if (outTick >= 0) lines.Add("end_tick=" + outTick.ToString(inv));
            if (replayEndTick > 0 && (inTick >= 0 || outTick >= 0)) lines.Add("demo_end_tick=" + replayEndTick.ToString(inv));
            if (!string.IsNullOrEmpty(preview)) lines.Add("preview=" + preview);
            return lines.ToArray();
        }

        // ================= LIBRARY =================

        private void ChooseLibrary_Click(object sender, RoutedEventArgs e)
        {
            // WPF on .NET Framework has no folder picker, so navigate into the
            // folder and accept the placeholder name.
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "Open the folder you want, then click Open";
            dlg.CheckFileExists = false;
            dlg.CheckPathExists = true;
            dlg.ValidateNames = false;
            dlg.FileName = "Select this folder";
            if (_library.Length > 0 && Directory.Exists(_library)) dlg.InitialDirectory = _library;

            if (dlg.ShowDialog() != true) return;

            string dir = Path.GetDirectoryName(dlg.FileName);
            if (dir != null && Directory.Exists(dir))
            {
                _library = dir;
                UpdateLibraryUi();
                SaveSettings();
                StatusText.Text = "Clips will be saved in " + _library;
            }
        }

        private void UpdateLibraryUi()
        {
            bool set = _library.Length > 0 && Directory.Exists(_library);
            LibraryText.Text = set ? _library : "(not set)";
            SetupPanel.Visibility = set ? Visibility.Collapsed : Visibility.Visible;
            LibraryRow.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= RECORDING =================

        private bool Recording
        {
            get { return _active != null && _active.IsRecording; }
        }

        private bool UseBuiltIn
        {
            get
            {
                var item = RecorderCombo.SelectedItem as ComboBoxItem;
                return item == null || (string)item.Tag != "obs";
            }
        }

        /// <summary>The recorder picked in settings, with its settings applied.</summary>
        private IRecorder GetRecorder()
        {
            if (!UseBuiltIn) return GetObs();

            if (_builtIn == null) _builtIn = new BuiltInRecorder(ProcessBox.Text.Trim());
            _builtIn.Fps = ParseBox(FpsBox, 60);
            _builtIn.Kbps = AutoKbpsBox.IsChecked == true ? 0 : ParseBox(KbpsBox, 7470);
            _builtIn.Profile = SelectedProfile;
            _builtIn.VolumeDb = VolumeSlider.Value;
            return _builtIn;
        }

        private H264Profile SelectedProfile
        {
            get
            {
                var item = ProfileCombo.SelectedItem as ComboBoxItem;
                H264Profile p;
                return item != null && Enum.TryParse((string)item.Tag, out p) ? p : H264Profile.Main;
            }
        }

        private void AutoKbpsBox_Changed(object sender, RoutedEventArgs e)
        {
            if (KbpsBox == null) return;
            KbpsBox.IsEnabled = AutoKbpsBox.IsChecked != true;
            UpdateAutoKbpsText();
        }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect { public int Left, Top, Right, Bottom; }

        private int _gameWidth, _gameHeight;       // BO2's last size seen while not minimised

        /// <summary>With Auto on, shows the bitrate BO2's current size would get, and puts it in the box.</summary>
        private void UpdateAutoKbpsText()
        {
            if (AutoKbpsText == null || AutoKbpsBox.IsChecked != true) { if (AutoKbpsText != null) AutoKbpsText.Text = ""; return; }

            const string Table = "7470 up to 1080p · 12000 at 1440p · 20000 at 4K";
            IntPtr window = Mem.ProcessId > 0 && Mem.ModuleBase != IntPtr.Zero ? TheaterKeys.FindWindow(Mem.ProcessId) : IntPtr.Zero;
            ClientRect r;
            if (window != IntPtr.Zero && GetClientRect(window, out r) && r.Right > 0 && r.Bottom > 0)
            {
                _gameWidth = r.Right;
                _gameHeight = r.Bottom;
            }
            // Exclusive fullscreen BO2 minimises to 0×0 when another window has
            // focus, so the last real size stands until it's back.

            if (window != IntPtr.Zero && _gameWidth > 0)
            {
                int kbps = BuiltInRecorder.AutoKbps(_gameWidth, _gameHeight);
                KbpsBox.Text = kbps.ToString(CultureInfo.InvariantCulture);
                AutoKbpsText.Text = "BO2 is " + _gameWidth + "×" + _gameHeight + ", so " + kbps + " kbps. Auto: " + Table;
            }
            else AutoKbpsText.Text = "Auto: " + Table;
        }

        private void RecorderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BuiltInPanel == null || ObsPanel == null) return;
            BuiltInPanel.Visibility = UseBuiltIn ? Visibility.Visible : Visibility.Collapsed;
            ObsPanel.Visibility = UseBuiltIn ? Visibility.Collapsed : Visibility.Visible;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeText == null) return;
            double db = VolumeSlider.Value;
            VolumeText.Text = db == 0 ? "0 dB (as played)"
                : (db > 0 ? "+" : "") + db.ToString("0") + " dB (×" + Math.Pow(10, db / 20).ToString(db < 0 ? "0.00" : "0.0") + ")";
            if (_builtIn != null) _builtIn.VolumeDb = db;
        }

        /// <summary>One connection, reused until the port or password changes.</summary>
        private ObsRecorder GetObs()
        {
            int port = ParseBox(ObsPortBox, DefObsPort);
            string password = ObsPasswordBox.Password;

            if (_obs != null && !_obs.UsesSettings(port, password) && !_obs.IsRecording)
            {
                _obs.Dispose();
                _obs = null;
            }
            if (_obs == null) _obs = new ObsRecorder("127.0.0.1", port, password);
            return _obs;
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleRecording();
        }

        /// <summary>
        /// Numpad / and the main button, one clip at a time:
        /// idle: check everything, capture the demo, record (or jump and arm, with a start tick set);
        /// jumping or armed: cancel; recording: stop, and the clip saves itself once it has a name;
        /// take waiting: save it.
        /// </summary>
        private void ToggleRecording()
        {
            if (_seeking) { _cancelSeek = true; return; }
            if (_recBusy || _saving) return;
            if (_armed) { CancelTake("Cancelled. Recording was armed but hadn't started."); return; }
            if (Recording) { StopRecording(-1, -1); return; }
            if (_takePath.Length > 0) { SaveClip(false); return; }
            BeginTake();
        }

        /// <summary>
        /// Everything that could make a take fail is checked before anything is
        /// recorded: the library, the game, the demo, and the recorder (a real
        /// round trip to OBS, or the built-in recorder's capture and encoder set
        /// up and waiting). The demo is captured here, so the save doesn't need
        /// the game later.
        /// </summary>
        private void BeginTake()
        {
            if (_library.Length == 0 || !Directory.Exists(_library))
            { Refuse("Choose where clips are saved first."); UpdateLibraryUi(); return; }
            if (!UpdateAttach()) { Refuse("Retail BO2 isn't running."); return; }
            if (!StartChainIfAny()) return;

            DemoOffsets offsets = SnapshotOffsets();
            IRecorder rec = GetRecorder();
            _active = rec;

            // Takes record into a staging folder under a neutral name. The clip
            // folder only appears at save time, named from the fields as they are then.
            string takeBase = Path.Combine(_library, TakesFolder,
                "take " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture));
            _takeBase = takeBase;

            SetRecBusy(true, rec is ObsRecorder ? "Checking OBS and capturing the demo…" : "Setting up the recorder and capturing the demo…");

            Thread t = new Thread(() =>
            {
                string error = null;
                DemoCapture cap = null;
                try
                {
                    rec.Preflight();
                    cap = DemoCapture.Read(offsets,
                        pct => Dispatcher.BeginInvoke(new Action(() => RipProgress.Value = pct)), out error);
                    if (cap != null) rec.Prepare(takeBase);
                }
                catch (Exception ex) { error = ex.Message; cap = null; }
                if (cap == null) { try { rec.Cancel(); } catch { } }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RipProgress.Value = 0;
                    SetRecBusy(false, null);
                    if (cap == null) { _chainIndex = -1; Refuse(error ?? "Couldn't start the take."); return; }

                    _capture = cap;
                    _takeNote = "";

                    int tick = 0, end;
                    // An exact tick counts only on the demo it was set on; a slider time works on any.
                    bool marked = (_markIn >= 0 ? _markDemo == cap.FileName : In1() >= 0) && TryReadTick(out tick, out end);
                    if (_chainIndex >= 0 && !marked)
                    { AbortChain("The clips' ticks were set on a different demo, or no replay is playing."); return; }
                    if (!marked) StartRecording(-1, -1);
                    else if (JumpToStartBox.IsChecked == true || _chainIndex >= 0) JumpAndArm();
                    else Arm(tick);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Ends a take that never recorded: releases what the recorder prepared.</summary>
        private void CancelTake(string status)
        {
            if (_chainIndex >= 0 && _chainTakes.Count > 0)
                status += " The " + _chainTakes.Count + " clip(s) already recorded stay in " + Path.Combine(_library, TakesFolder) + ".";
            _chainIndex = -1;
            _chainTakes.Clear();
            _chainNotes.Clear();
            _capture = null;
            IRecorder rec = _active;
            if (rec != null && !rec.IsRecording)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { rec.Cancel(); } catch { }
                    TryDeleteEmptyDir(Path.Combine(_library, TakesFolder));
                });
            Disarm(status);
        }

        // ================= JUMP TO START TICK =================

        private TheaterSeek NewSeek(int offTick, TheaterKeys keys)
        {
            var clock = Stopwatch.StartNew();
            return new TheaterSeek(
                () => Mem.ReadInt(Mem.At(offTick)),
                keys.Press,
                () => clock.ElapsedMilliseconds,
                ms => Thread.Sleep(ms),
                () => _cancelSeek);
        }

        /// <summary>BO2's window, and the keys set up from settings. Null with a status when BO2 has none.</summary>
        private TheaterKeys GetTheaterKeys()
        {
            IntPtr window = TheaterKeys.FindWindow(Mem.ProcessId);
            if (window == IntPtr.Zero) { Refuse("Can't find BO2's window to send the theater keys to."); return null; }
            return new TheaterKeys(window) { FakeActivation = FakeActivationBox.IsChecked == true };
        }

        /// <summary>
        /// Keys only go to a replay: the tick and end tick must read as one, and a
        /// demo must be loaded. Never while BO2 is anywhere else.
        /// </summary>
        private bool ReplayOpen(out int tick, out int end)
        {
            return TryReadTick(out tick, out end) && Mem.ReadAscii(Mem.At(OffFileName), 64).Length > 0;
        }

        /// <summary>
        /// Record with a start tick: jump to the jump point before it (from ahead of
        /// it or behind it), arm, then play. Recording starts when the replay
        /// crosses the start tick, as with arming by hand.
        /// </summary>
        private void JumpAndArm()
        {
            int tick, end;
            if (!ReplayOpen(out tick, out end)) { Arm(tick); return; }
            TheaterKeys keys = GetTheaterKeys();
            if (keys == null) { Arm(tick); return; }

            int start = CurIn();
            int offTick = OffTick;
            _seeking = true;
            _cancelSeek = false;
            StatusText.Text = (_chainIndex >= 0 ? "Clip " + (_chainIndex + 1) + " of " + ClipCount + ": jumping to " : "Jumping to ") + FormatTime(TickSeconds(start)) + "… Numpad / cancels.";
            UpdateRecordUi();

            Thread t = new Thread(() =>
            {
                TheaterSeek seek = NewSeek(offTick, keys);
                TheaterSeek.Result r = new TheaterSeek.Result();
                string error = null;
                bool cancelled = false, playing = false;
                try
                {
                    r = seek.ToJumpPointBefore(start, end);
                    if (r.Ok)
                    {
                        // Armed before playing, so the crossing can't be missed.
                        Dispatcher.Invoke(new Action(() => { if (!_cancelSeek) ArmQuiet(); }));
                        if (_cancelSeek) throw new SeekCancelledException();
                        playing = seek.Play();
                    }
                }
                catch (SeekCancelledException) { cancelled = true; }
                catch (Exception ex) { error = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _seeking = false;
                    _takeNote = "jump: " + seek.Log.ToString().Trim().Replace(Environment.NewLine, "; ")
                                + (_takeNote.Length > 0 ? "\r\n" + _takeNote : "");
                    string at = FormatTime(TickSeconds(start)) + " (tick " + start + ")";

                    if (cancelled) { CancelTake("Cancelled while jumping to the start tick."); return; }
                    if (error != null || !r.Ok)
                    {
                        // Fall back to arming by hand rather than losing the take.
                        if (!_armed) ArmQuiet();
                        Refuse("Couldn't jump to the start tick: " + (error ?? r.Message)
                               + " Armed anyway: jump back before " + at + " and play. Numpad / cancels.");
                        return;
                    }
                    if (!_armed) return;   // cancelled between the jump and arming
                    StatusText.Text = (playing
                        ? "Armed at " + FormatTime(TickSeconds(r.Tick)) + ". Playing; recording starts at " + at + ". Numpad / cancels."
                        : "Armed at " + FormatTime(TickSeconds(r.Tick)) + ", but BO2 didn't start playing. Press play; recording starts at " + at + ".")
                        + (r.Message.Length > 0 ? "\r\n" + r.Message : "");
                    MarkBeep(playing ? MarkFeedback.Armed : MarkFeedback.Failed);
                    UpdateRecordUi();
                }));
            });
            t.IsBackground = true;
            t.Name = "Jump to start tick";
            t.Start();
        }

        private void GoToStartButton_Click(object sender, RoutedEventArgs e)
        {
            GoToStart();
        }

        /// <summary>
        /// Numpad + and the Go to button: jump near the start tick, play, and pause
        /// just before it. Pressing again while it runs cancels.
        /// </summary>
        private void GoToStart()
        {
            if (_seeking) { _cancelSeek = true; return; }
            if (_armed || Recording || _recBusy) { Refuse("Not while a take is armed or recording."); return; }
            if (In1() < 0) { Refuse("Set the clip's start first: drag the sliders, or numpad * during the replay."); return; }

            int tick, end;
            if (!ReplayOpen(out tick, out end)) { Refuse("No replay open in retail BO2."); return; }
            if (_markIn >= 0 && Mem.ReadAscii(Mem.At(OffFileName), 64) != _markDemo) { Refuse("The start tick was set on a different demo."); return; }
            TheaterKeys keys = GetTheaterKeys();
            if (keys == null) return;

            int start = In1();
            int offTick = OffTick;
            int lead = _pauseLeadMs;
            _seeking = true;
            _goToRunning = true;
            _cancelSeek = false;
            StatusText.Text = "Going to " + FormatTime(TickSeconds(start)) + "… Numpad + cancels.";
            UpdateRecordUi();

            Thread t = new Thread(() =>
            {
                TheaterSeek seek = NewSeek(offTick, keys);
                TheaterSeek.Result r = new TheaterSeek.Result();
                string error = null, jumpNote = "";
                bool cancelled = false;
                try
                {
                    r = seek.ToJumpPointBefore(start, end);
                    if (r.Ok)
                    {
                        jumpNote = r.Message;
                        if (jumpNote.Length > 0)
                        {
                            string note = jumpNote;
                            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "Playing to the start tick. " + note));
                        }
                        r = seek.PlayAndPauseAt(start, lead);
                    }
                }
                catch (SeekCancelledException) { cancelled = true; }
                catch (Exception ex) { error = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _seeking = false;
                    _goToRunning = false;
                    InfoBox.Text = "Go to start tick " + start + "\r\n\r\n" + seek.Log;
                    UpdateRecordUi();

                    if (cancelled) { StatusText.Text = "Stopped going to the start tick."; return; }
                    if (error != null || !r.Ok) { Refuse("Couldn't go to the start tick: " + (error ?? r.Message)); return; }

                    // Aim a couple of ms before the start tick so arming from here
                    // still sees the replay cross it; learn the key-to-pause delay.
                    int miss = r.Tick - (start - 2);
                    _pauseLeadMs = Math.Max(0, Math.Min(100, _pauseLeadMs + miss / 2));

                    int off = r.Tick - start;
                    StatusText.Text = "Paused at " + FormatTime(TickSeconds(r.Tick)) + " (tick " + r.Tick + ", "
                                      + (off == 0 ? "on" : Math.Abs(off) + " ms " + (off < 0 ? "before" : "after")) + " the start tick)."
                                      + (jumpNote.Length > 0 ? "\r\n" + jumpNote : "");
                    MarkBeep(MarkFeedback.In);
                }));
            });
            t.IsBackground = true;
            t.Name = "Go to start tick";
            t.Start();
        }

        /// <summary>Status plus the double low beep, the only thing heard from inside the game.</summary>
        private void Refuse(string status)
        {
            StatusText.Text = status;
            MarkBeep(MarkFeedback.Failed);
        }

        private DemoOffsets SnapshotOffsets()
        {
            return new DemoOffsets
            {
                DemoData = OffDemoData,
                FileName = OffFileName,
                Thumb = OffThumb,
                Summary = OffSummary,
                SummaryLength = OffSummaryLength
            };
        }

        /// <summary>A dead OBS while armed would otherwise only surface at the start tick.</summary>
        private void CheckObsWhileArmed()
        {
            var obs = _active as ObsRecorder;
            if (!_armed || obs == null || obs.IsConnected) return;

            _armed = false;
            _capture = null;
            Refuse("Lost the connection to OBS while armed, so recording was cancelled. "
                   + "Check OBS, then press Record again.");
        }

        /// <summary>Starts the watcher on the start tick, without status or beep.</summary>
        private void ArmQuiet()
        {
            _watchOffTick = OffTick;
            _watchIn = CurIn();
            _armed = true;
            RefreshWatch();
        }

        private void Arm(int now)
        {
            ArmQuiet();

            string at = FormatTime(TickSeconds(CurIn())) + " (tick " + CurIn() + ")";
            StatusText.Text = now <= CurIn()
                ? "Armed. Recording starts when the replay reaches " + at + ". Numpad / again cancels."
                : "Armed, but the replay is already past " + at + ". Jump back to a snapshot before it and play. "
                  + "Numpad / again cancels.";
            MarkBeep(MarkFeedback.Armed);
        }

        private void Disarm(string status)
        {
            _armed = false;
            StatusText.Text = status;
            UpdateRecordUi();
        }

        /// <summary>
        /// The replay reached the start tick: it came from below, or sat exactly
        /// on it (paused where the start tick was set) and began playing.
        /// </summary>
        private static bool CrossedStart(int prev, int tick, int startTick)
        {
            return tick >= startTick && prev <= startTick && tick > prev;
        }

        /// <summary>The replay played past the end tick, not jumped back from beyond it.</summary>
        private static bool CrossedEnd(int prev, int tick, int endTick)
        {
            return prev < endTick && tick >= endTick;
        }

        private void WatchTicks()
        {
            int prev = -1;
            while (true)
            {
                if (!_armed && !_autoEndActive) { prev = -1; Thread.Sleep(50); continue; }

                int tick = Mem.ReadInt(Mem.At(_watchOffTick));

                // A failed read comes back as 0. Taking it as a real tick would
                // make the next good read look like a jump across the start tick.
                if (tick <= 0) { Thread.Sleep(5); continue; }

                if (prev >= 0)
                {
                    if (_armed && CrossedStart(prev, tick, _watchIn))
                    {
                        _armed = false;
                        int mark = _watchIn, seen = tick;
                        Dispatcher.BeginInvoke(DispatcherPriority.Send,
                            new Action(() => StartRecording(mark, seen)));
                    }
                    else if (_autoEndActive && CrossedEnd(prev, tick, _watchOut))
                    {
                        _autoEndActive = false;
                        int mark = _watchOut, seen = tick;
                        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                        {
                            if (!_recBusy && Recording) StopRecording(mark, seen);
                        }));
                    }
                }
                prev = tick;
                Thread.Sleep(5);   // the OS rounds this up to its ~16 ms timer, about a frame
            }
        }

        /// <summary>Re-points the watcher after marks, the toggle, or recording state change.</summary>
        private void RefreshWatch()
        {
            if (_armed)
            {
                if (CurIn() < 0) { CancelTake("Start tick cleared, so recording is no longer armed."); }
                else _watchIn = CurIn();
            }

            if (Recording && (AutoEndBox.IsChecked == true || _chainIndex >= 0) && CurOut() >= 0)
            {
                _watchOffTick = OffTick;
                _watchOut = CurOut();
                _autoEndActive = true;
            }
            else _autoEndActive = false;

            UpdateRecordUi();
        }

        private void AutoEndBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_ready) RefreshWatch();
        }

        /// <summary>The window needs a handle before a hotkey can be tied to it.</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _hwnd = (HwndSource)PresentationSource.FromVisual(this);
            ThemeTitleBar(_hwnd.Handle);
            _hwnd.AddHook(HotkeyHook);

            RegisterAllHotkeys();
            UpdateRecordUi();
        }

        private void RegisterOrReport(int id, uint vk, string label, List<string> failed)
        {
            if (RegisterHotKey(_hwnd.Handle, id, MOD_NOREPEAT, vk)) _hotkeys.Add(id);
            else failed.Add(label + " (win32 error " + Marshal.GetLastWin32Error() + ")");
        }

        private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY) return IntPtr.Zero;

            switch (wParam.ToInt32())
            {
                case HotkeyRecord: handled = true; ToggleRecording(); break;
                case HotkeyMarkIn: handled = true; SetMarkFromHotkey(true); break;
                case HotkeyMarkOut: handled = true; SetMarkFromHotkey(false); break;
                case HotkeyGoToStart: handled = true; GoToStart(); break;
            }
            return IntPtr.Zero;
        }

        /// <param name="startTick">The start tick that triggered this, or -1 for a manual start.</param>
        /// <param name="seenTick">The tick the watcher saw when it fired, or -1.</param>
        private void StartRecording(int startTick, int seenTick)
        {
            if (_library.Length == 0 || !Directory.Exists(_library))
            { Refuse("Choose where clips are saved first."); UpdateLibraryUi(); return; }

            // BeginTake picked the recorder and the path, and prepared it.
            string staging = Path.Combine(_library, TakesFolder);
            string takeBase = _takeBase.Length > 0 ? _takeBase
                : Path.Combine(staging, "take " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture));

            int offTick = OffTick;
            IRecorder rec = _active ?? GetRecorder();
            _active = rec;
            SetRecBusy(true, startTick >= 0 ? "Start tick reached, starting the recording…" : "Starting the recording…");

            Thread t = new Thread(() =>
            {
                string error = null;
                int confirmedTick = -1;
                try
                {
                    rec.Start(takeBase);
                    // How far the replay ran while the recorder started: the head
                    // the recording is missing. Measured so a pre-roll can be tuned.
                    if (startTick >= 0) confirmedTick = Mem.ReadInt(Mem.At(offTick));
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    TryDeleteEmptyDir(staging);
                }
                string warning = rec.LastWarning;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    string status = error ?? "Recording. The clip saves when the recording ends.";
                    if (error == null)
                    {
                        _recClock.Restart();
                        if (confirmedTick >= 0)
                            _takeNote += (_takeNote.Length > 0 ? "\r\n" : "")
                                        + "start tick " + startTick + ", watcher fired at " + seenTick
                                        + " (+" + (seenTick - startTick) + " ms), " + rec.Name + " started at " + confirmedTick
                                        + " (+" + (confirmedTick - startTick) + " ms)";
                    }
                    else _capture = null;

                    SetRecBusy(false, status);
                    if (error != null) MarkBeep(MarkFeedback.Failed);
                    if (warning != null) StatusText.Text += "\r\n" + warning;
                    if (error == null) RefreshWatch();
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <param name="endTick">The end tick that triggered this, or -1 for a manual stop.</param>
        /// <param name="seenTick">The tick the watcher saw when it fired, or -1.</param>
        private void StopRecording(int endTick, int seenTick)
        {
            _autoEndActive = false;
            IRecorder rec = _active;
            if (rec == null) return;
            string who = rec is ObsRecorder ? "OBS" : "the recorder";
            SetRecBusy(true, endTick >= 0
                ? "End tick reached. Stopping, waiting for " + who + " to finish the file…"
                : "Stopping, waiting for " + who + " to finish the file…");

            Thread t = new Thread(() =>
            {
                string path = null, error = null;
                try { path = rec.Stop(); }
                catch (Exception ex) { error = ex.Message; }
                var builtIn = rec as BuiltInRecorder;
                string warning = rec.LastWarning;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _recClock.Stop();
                    if (builtIn != null && builtIn.Stats != null)
                        _takeNote += (_takeNote.Length > 0 ? "\r\n" : "") + builtIn.Stats;
                    // A warning that arrives with a file goes into the take's notes:
                    // OBS's remux note is exactly that case.
                    if (warning != null && path != null && rec is ObsRecorder)
                        _takeNote += (_takeNote.Length > 0 ? "\r\n" : "") + warning;
                    if (warning != null && error == null && path == null) error = warning;
                    if (endTick >= 0)
                        _takeNote += (_takeNote.Length > 0 ? "\r\n" : "") + "auto-stopped at end tick " + endTick
                                     + ", watcher fired at " + seenTick + " (+" + (seenTick - endTick) + " ms)";
                    SetRecBusy(false, null);
                    if (error != null) Refuse(error);
                    else FinishPreview(path);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>The recording ended with a known file. The demo is already captured, so save.</summary>
        private void FinishPreview(string path)
        {
            if (_chainIndex >= 0) { ChainTookTake(path); return; }
            _takePath = path ?? "";
            if (path == null)
            {
                _capture = null;
                Refuse("Recording ended, but " + (_active is ObsRecorder ? "OBS didn't report the file" : "the file wasn't finished")
                       + ". Anything written is in "
                       + Path.Combine(_library, TakesFolder) + ".");
                UpdateRecordUi();
                return;
            }
            SaveClip(true);
        }

        private void SetRecBusy(bool busy, string status)
        {
            _recBusy = busy;
            if (status != null) StatusText.Text = status;
            UpdateRecordUi();
        }

        private void UpdateRecordUi()
        {
            bool recording = Recording;

            // Ended without DemoRipper stopping it: OBS's own button or hotkey,
            // the connection dropped, or BO2 closed under the built-in recorder.
            if (!recording && _recClock.IsRunning && !_recBusy)
            {
                _recClock.Stop();
                _autoEndActive = false;
                var obs = _active as ObsRecorder;
                if (obs != null && !obs.IsConnected)
                {
                    // Only the connection died; OBS keeps writing the file it started.
                    _takePath = obs.CurrentPath ?? "";
                    Refuse("Lost the connection to OBS while recording. OBS may still be recording to "
                           + (_takePath.Length > 0 ? _takePath : Path.Combine(_library, TakesFolder))
                           + ". Stop it in OBS, then press Save clip.");
                }
                else
                {
                    if (_active != null && _active.LastWarning != null) StatusText.Text = _active.LastWarning;
                    FinishPreview(_active != null ? _active.LastStoppedPath : null);
                }
            }

            bool takeWaiting = !recording && _takePath.Length > 0;

            RecordButton.Content = _saving ? "Saving…"
                : recording ? "■ Stop recording"
                : _seeking && !_goToRunning ? "◌ Jumping (cancel)"
                : _armed ? "◌ Armed (cancel)"
                : takeWaiting ? "Save clip"
                : (object)RecordIdleLabel;
            RecordButton.IsEnabled = (!_recBusy && !_saving) || _seeking;
            DiscardButton.Visibility = takeWaiting && !_saving ? Visibility.Visible : Visibility.Collapsed;
            RipButton.IsEnabled = !_recBusy && !_saving && !_armed && !_seeking && !recording && !takeWaiting;
            GoToStartButton.Content = _goToRunning ? "Stop" : "Go to";
            GoToStartButton.IsEnabled = _goToRunning || (!_seeking && !_armed && !recording && !_recBusy && !_saving);

            string clipTag = _chainIndex >= 0 ? "CLIP " + (_chainIndex + 1) + "/" + ClipCount + "   " : "";
            RecordText.Text = clipTag + (recording
                ? "REC  " + FormatTime((int)_recClock.Elapsed.TotalSeconds)
                  + (_autoEndActive ? "   ends at " + FormatTime(TickSeconds(_watchOut)) : "")
                : _armed ? "ARMED  starts at " + FormatTime(TickSeconds(_watchIn)) + "  (tick " + _watchIn + ")"
                : takeWaiting ? (_chainTakes.Count > 1 ? _chainTakes.Count + " TAKES" : "TAKE  " + Path.GetFileName(_takePath)) + "   not saved yet"
                : "");
            UpdateMultiUi();
        }

        /// <summary>
        /// Writes the clip folder: the four demo files from the capture, the take
        /// moved in (when there is one), and clip.txt. Automatic saves never show
        /// a dialog over the game; a missing name or an existing folder leaves the
        /// take waiting instead.
        /// </summary>
        private void SaveClip(bool auto)
        {
            if (_capture == null) { Refuse("Nothing captured to save. Press Record to start a take."); UpdateRecordUi(); return; }
            if (_library.Length == 0 || !Directory.Exists(_library))
            { Refuse("Choose where clips are saved first."); UpdateLibraryUi(); return; }

            if (!HasClipName)
            {
                ShowNameWarning();
                Refuse(_takePath.Length > 0
                    ? "Take recorded. Name the clip, then press Save clip (or numpad /)."
                    : "Give the clip a name.");
                UpdateRecordUi();
                return;
            }

            string folderName = BuildFolderName();
            string folder = Path.Combine(_library, folderName);
            if (Directory.Exists(folder))
            {
                if (auto)
                {
                    Refuse("\"" + folderName + "\" already exists. Rename the clip, or press Save clip to write into it.");
                    UpdateRecordUi();
                    return;
                }
                var ask = MessageBox.Show("\"" + folderName + "\" already exists. Write into it anyway?",
                    "Folder exists", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes) { StatusText.Text = "Not saved."; return; }
            }

            // These read UI controls and fields, so snapshot before the worker starts.
            DemoCapture cap = _capture;
            string takePath = _takePath;
            string library = _library;
            string takeNote = _takeNote;
            Maps.Entry picked = SelectedMap;
            Maps.Entry map = picked != null && picked.Id.Length > 0 ? picked : cap.Map;
            int startSec, endSec;
            ParseTime(StartBox.Text, out startSec);
            ParseTime(EndBox.Text, out endSec);
            startSec = Clamp(startSec);
            endSec = Clamp(endSec);

            // Ticks are only saved against the demo they were marked on.
            bool marksHere = _markDemo.Length > 0 && _markDemo == cap.FileName;
            bool marksElsewhere = _markDemo.Length > 0 && !marksHere && (_markIn >= 0 || _markOut >= 0);
            int markIn = marksHere ? _markIn : _markIn < 0 ? In1() : -1;
            int markOut = marksHere ? _markOut : _markOut < 0 ? Out1() : -1;
            int markEnd = marksHere ? _markEndTick : 0;

            // Several clips from one demo: each take moves in under its own clip's name.
            bool multi = _chainTakes.Count > 1;
            Maps.Entry tokenMap = SelectedMap;
            List<KeyValuePair<string, string>> chainFiles = multi ? ChainFiles(PlayerText, tokenMap != null ? tokenMap.Token : "") : null;
            List<string> extraLines = multi
                ? ExtraManifestLines(chainFiles.Select(f => f.Value + Path.GetExtension(f.Key)).ToList())
                : new List<string>();
            string player = PlayerText;

            _saving = true;
            SetBusy(true, "Saving " + folderName + "…");

            Thread t = new Thread(() =>
            {
                string log;
                bool ok = false;
                string movedPreview = takePath;

                try
                {
                    Directory.CreateDirectory(folder);

                    string basePath = Path.Combine(folder, cap.DemoFile);
                    byte[] tags = Tags.Build(map != null ? map.Number : 0, cap.ModeNumber);
                    // A map or mode the game's tables don't know is written as 0; say so rather than pass it off.
                    string tagsNote = (map == null ? "   MAP NOT RECOGNISED: written as 0 (Aftermath); pick the map and save again" : "")
                                    + (cap.ModeNumber == 0 ? "   MODE NOT RECOGNISED (\"" + cap.Mode + "\"): written as 0" : "");

                    File.WriteAllBytes(basePath, cap.Demo);
                    File.WriteAllBytes(basePath + ".summary", cap.Summary);
                    File.WriteAllBytes(basePath + ".tags", tags);
                    File.WriteAllBytes(basePath + ".thumbnail", cap.Thumbnail);

                    StringBuilder previewLog = new StringBuilder();
                    if (multi)
                    {
                        string first = null, stuck = null;
                        foreach (var f in chainFiles)
                        {
                            string m = MovePreview(f.Key, library, folder, f.Value, previewLog);
                            if (first == null) first = m;
                            bool there = !string.IsNullOrEmpty(m) && string.Equals(Path.GetDirectoryName(m), folder, StringComparison.OrdinalIgnoreCase);
                            if (!there && stuck == null) stuck = f.Key;
                        }
                        movedPreview = stuck ?? first;
                    }
                    else movedPreview = MovePreview(takePath, library, folder, folderName, previewLog);

                    bool previewInFolder = !string.IsNullOrEmpty(movedPreview) && File.Exists(movedPreview)
                        && string.Equals(Path.GetDirectoryName(movedPreview), folder, StringComparison.OrdinalIgnoreCase);
                    var manifest = new List<string>(BuildManifest(
                        folderName, cap.DemoFile, map != null ? map.Id : "", cap.Mode, cap.Demo.Length, DateTime.Now,
                        startSec, endSec, markIn, markOut, markEnd,
                        previewInFolder ? Path.GetFileName(multi ? chainFiles[0].Value + Path.GetExtension(chainFiles[0].Key) : movedPreview) : null,
                        player));
                    manifest.AddRange(extraLines);
                    File.WriteAllLines(Path.Combine(folder, "clip.txt"), manifest);

                    log = folderName + "\r\n\r\n"
                        + cap.DemoFile + "   " + cap.Demo.Length + " bytes\r\n"
                        + "  .summary    " + cap.Summary.Length + "\r\n"
                        + "  .tags       " + tags.Length + "   map " + (map != null ? map.Id : "unknown") + tagsNote + "\r\n"
                        + "  .thumbnail  " + cap.Thumbnail.Length
                        + (takePath.Length == 0 ? "\r\nno preview (saved without recording)" : previewLog.ToString())
                        + "\r\nclip.txt   "
                        + (markIn >= 0 || markOut >= 0
                            ? "ticks " + (markIn >= 0 ? markIn.ToString() : "-") + " to " + (markOut >= 0 ? markOut.ToString() : "-")
                            : "no tick marks")
                        + (marksElsewhere ? "\r\nTicks were set on a different demo, so they weren't saved." : "")
                        + (takeNote.Length > 0 ? "\r\n\r\n" + takeNote : "");
                    ok = true;
                }
                catch (Exception ex) { log = "Save failed: " + ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _saving = false;
                    InfoBox.Text = log;
                    SetBusy(false, null);

                    if (!ok)
                    {
                        Refuse("Did not save." + (takePath.Length > 0 ? " The take is still waiting." : ""));
                    }
                    else
                    {
                        bool inFolder = !string.IsNullOrEmpty(movedPreview) && string.Equals(
                            Path.GetDirectoryName(movedPreview), folder, StringComparison.OrdinalIgnoreCase);

                        ResultBox.Text = folderName;
                        _lastFolder = folder;
                        OpenFolderButton.IsEnabled = true;

                        if (multi && !inFolder)
                        {
                            // The demo files and the takes that moved are in; the rest stay in _takes.
                            _takePath = "";
                            _capture = null;
                            _chainTakes.Clear();
                            _chainNotes.Clear();
                            Refuse("Saved " + folderName + ", but a take didn't move (see the log). It stays in "
                                   + Path.Combine(library, TakesFolder) + ".");
                        }
                        else if (takePath.Length == 0 || inFolder)
                        {
                            _chainTakes.Clear();
                            _chainNotes.Clear();
                            _takePath = "";
                            _capture = null;
                            _takeNote = "";
                            StatusText.Text = "Saved " + folderName + ".";
                            MarkBeep(MarkFeedback.Saved);
                        }
                        else
                        {
                            // Demo files are written but the preview didn't move
                            // (OBS still remuxing, say). Keep the take so Save retries.
                            _takePath = movedPreview;
                            Refuse("Saved the demo files, but the preview didn't move. See the log, then press Save clip again.");
                        }
                    }
                    UpdateRecordUi();
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DiscardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_takePath.Length == 0) return;

            var takes = _chainTakes.Count > 1 ? new List<string>(_chainTakes) : new List<string> { _takePath };
            var ask = MessageBox.Show((takes.Count > 1
                                          ? "Delete the " + takes.Count + " recorded takes of this demo's clips"
                                          : "Delete the recorded take \"" + Path.GetFileName(_takePath) + "\"")
                                      + "? " + (takes.Count > 1 ? "They haven't" : "It hasn't") + " been saved to a clip folder.",
                "Discard take", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return;

            string staging = Path.Combine(_library, TakesFolder);
            int deleted = 0;

            foreach (string take in takes)
            {
                string dir = Path.GetDirectoryName(take);
                // Only ever delete inside the takes folder DemoRipper records into.
                if (!string.Equals(dir, staging, StringComparison.OrdinalIgnoreCase)) continue;
                string baseName = Path.GetFileNameWithoutExtension(take);
                try
                {
                    foreach (string f in Directory.GetFiles(dir, baseName + ".*"))
                    {
                        if (!string.Equals(Path.GetFileNameWithoutExtension(f), baseName, StringComparison.OrdinalIgnoreCase)) continue;
                        File.Delete(f);
                        deleted++;
                    }
                    TryDeleteEmptyDir(dir);
                }
                catch (IOException ex) { StatusText.Text = "Couldn't delete the take: " + ex.Message; return; }
            }
            _chainTakes.Clear();
            _chainNotes.Clear();

            StatusText.Text = deleted > 0
                ? "Take discarded (" + deleted + " file" + (deleted == 1 ? "" : "s") + " deleted)."
                : "Take forgotten. Its file was outside the takes folder, so it was left at " + _takePath + ".";
            _takePath = "";
            _capture = null;
            _takeNote = "";
            UpdateRecordUi();
        }

        private void ObsTestButton_Click(object sender, RoutedEventArgs e)
        {
            ObsRecorder obs = GetObs();
            SaveSettings();
            ObsTestButton.IsEnabled = false;
            StatusText.Text = "Connecting to OBS…";

            Thread t = new Thread(() =>
            {
                StringBuilder sb = new StringBuilder();
                bool ok = false;
                try
                {
                    obs.EnsureConnected();
                    sb.AppendLine("OBS            " + obs.ObsVersion);
                    sb.AppendLine("obs-websocket  " + obs.WebSocketVersion);
                    sb.AppendLine("record folder  " + obs.GetRecordDirectory());
                    sb.AppendLine("state          " + (obs.IsRecording ? "recording" : "idle"));
                    ok = true;
                }
                catch (Exception ex) { sb.AppendLine(ex.Message); }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InfoBox.Text = sb.ToString();
                    StatusText.Text = ok ? "Connected to OBS." : "Couldn't connect to OBS.";
                    ObsTestButton.IsEnabled = true;
                    UpdateRecordUi();
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// Moves a recorded preview into the clip folder as "folder name.ext".
        /// Files OBS wrote with the same base name (an auto-remuxed .mp4 beside
        /// the .mkv) move with it. Returns the preview's new path.
        /// </summary>
        private static string MovePreview(string previewPath, string library, string folder,
                                          string folderName, StringBuilder log)
        {
            if (string.IsNullOrEmpty(previewPath)) return previewPath;

            // OBS joins the record folder and file name with '/'. Directory.GetFiles
            // hands back backslashes, so without normalising, the main file is never
            // recognised: it still moves, but the old path comes back and the take
            // looks unsaved.
            previewPath = Path.GetFullPath(previewPath);
            if (!File.Exists(previewPath)) return previewPath;

            string srcDir = Path.GetDirectoryName(previewPath);
            string srcBase = Path.GetFileNameWithoutExtension(previewPath);
            string result = previewPath;

            foreach (string src in Directory.GetFiles(srcDir, srcBase + ".*"))
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(src), srcBase,
                                   StringComparison.OrdinalIgnoreCase)) continue;

                string dest = Path.Combine(folder, folderName + Path.GetExtension(src));
                bool main = string.Equals(src, previewPath, StringComparison.OrdinalIgnoreCase);

                if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
                {
                    log.Append("\r\n" + Path.GetFileName(dest) + "   preview");
                    continue;
                }
                if (File.Exists(dest))
                {
                    log.Append("\r\nPreview not moved — " + Path.GetFileName(dest) + " already exists.");
                    continue;
                }

                try
                {
                    File.Move(src, dest);
                    if (main) result = dest;
                    log.Append("\r\n" + Path.GetFileName(dest) + "   preview");
                }
                catch (IOException ex)
                {
                    log.Append("\r\nPreview not moved — " + ex.Message
                               + " OBS may still be remuxing; it's at " + src);
                }
            }

            // Only ever a folder DemoRipper recorded into, directly inside the library.
            if (!string.Equals(srcDir, folder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(srcDir), library, StringComparison.OrdinalIgnoreCase))
                TryDeleteEmptyDir(srcDir);

            return result;
        }

        /// <summary>Non-recursive, so it can only ever remove a folder that is already empty.</summary>
        private static void TryDeleteEmptyDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir, false);
            }
            catch { }
        }

        /// <summary>DPAPI, current Windows user — the settings file is plain text beside the exe.</summary>
        private static string Protect(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
        }

        private static string Unprotect(string stored)
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }

        // ================= READ =================

        /// <summary>
        /// Shows what's loaded and selects its map. Optional: Record and Save
        /// without recording capture the demo themselves.
        /// </summary>
        private void ReadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!UpdateAttach()) { StatusText.Text = "Retail BO2 isn't running."; return; }

            StringBuilder sb = new StringBuilder();
            try
            {
                int dataPtr = Mem.ReadInt(Mem.At(OffDemoData));
                int dataSize = Mem.ReadInt(Mem.At(OffDemoData + 4));
                int summaryLen = Mem.ReadInt(Mem.At(OffSummaryLength));
                string fileName = Mem.ReadAscii(Mem.At(OffFileName), 64);

                sb.AppendLine("name        " + (fileName.Length > 0 ? fileName : "(empty)"));
                sb.AppendLine("size        " + dataSize
                              + "  (" + (dataSize / 1048576.0).ToString("0.0") + " MB)");
                sb.AppendLine("summary     " + summaryLen + " bytes");

                if (_markDemo.Length > 0 && _markDemo != fileName)
                {
                    sb.AppendLine("ticks       cleared, they were set on " + _markDemo);
                    ClearMarks();
                }

                if (dataPtr <= 0x10000 || dataSize <= 0 || dataSize >= MaxDemoBytes)
                {
                    sb.AppendLine();
                    sb.AppendLine("No demo loaded, or the offsets have moved.");
                    InfoBox.Text = sb.ToString();
                    StatusText.Text = "Nothing usable in memory.";
                    return;
                }

                Maps.Entry map = Maps.FromFileName(fileName);
                int us = fileName.IndexOf('_');
                string mode = us > 0 ? fileName.Substring(0, us) : "";
                int modeNumber;
                bool gmOk = Tags.TryGameMode(mode, out modeNumber);

                sb.AppendLine("mode        " + mode + (gmOk ? "" : "   UNKNOWN"));
                sb.AppendLine("map         " + (map != null ? map.Id + "  (" + map.Display + ")" : "UNKNOWN"));
                sb.AppendLine();
                SelectMap(map);

                int sample = Math.Min(dataSize, 4 * 1024 * 1024);
                int zeros = Mem.LongestZeroRun(Mem.ReadBytes((IntPtr)dataPtr, sample));
                sb.AppendLine(zeros >= ZeroRunWarn
                    ? "Still downloading — " + zeros + " zero bytes in a row. Wait and read again."
                    : "Demo looks complete.");

                StatusText.Text = zeros >= ZeroRunWarn ? "Demo still downloading." : "Demo ready.";
            }
            catch (Exception ex)
            {
                sb.AppendLine("Read failed: " + ex.Message);
                StatusText.Text = "Read failed.";
            }

            InfoBox.Text = sb.ToString();
        }

        // ================= RIP =================

        /// <summary>Saves a clip folder with no preview ("Rip demo only").</summary>
        private void RipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recBusy || _saving || _armed || _seeking || Recording || _takePath.Length > 0)
            { StatusText.Text = "Finish or discard the current take first."; return; }
            if (!HasClipName) { ShowNameWarning(); StatusText.Text = "Give the clip a name."; return; }
            if (_library.Length == 0 || !Directory.Exists(_library))
            { StatusText.Text = "Choose where clips are saved first."; UpdateLibraryUi(); return; }
            if (!UpdateAttach()) { StatusText.Text = "Retail BO2 isn't running."; return; }

            DemoOffsets offsets = SnapshotOffsets();
            SetBusy(true, "Capturing the demo…");

            Thread t = new Thread(() =>
            {
                string error = null;
                DemoCapture cap = null;
                try
                {
                    cap = DemoCapture.Read(offsets,
                        pct => Dispatcher.BeginInvoke(new Action(() => RipProgress.Value = pct)), out error);
                }
                catch (Exception ex) { error = ex.Message; }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SetBusy(false, null);
                    if (cap == null) { StatusText.Text = error ?? "Couldn't capture the demo."; return; }

                    _capture = cap;
                    _takePath = "";
                    _takeNote = "";
                    SaveClip(false);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetBusy(bool busy, string status)
        {
            ReadButton.IsEnabled = !busy;
            RelocateButton.IsEnabled = !busy;
            if (busy) RipButton.IsEnabled = false;
            else
            {
                RipProgress.Value = 0;
                UpdateRecordUi();
            }
            if (status != null) StatusText.Text = status;
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastFolder.Length == 0 || !Directory.Exists(_lastFolder)) return;
            try { Process.Start(_lastFolder); }
            catch (Exception ex) { StatusText.Text = "Couldn't open folder: " + ex.Message; }
        }

        private void CopyResult_Click(object sender, RoutedEventArgs e)
        {
            if (ResultBox.Text.Length == 0) { StatusText.Text = "Nothing to copy."; return; }
            try { Clipboard.SetText(ResultBox.Text); StatusText.Text = "Copied."; }
            catch (Exception ex) { StatusText.Text = "Copy failed: " + ex.Message; }
        }

        // ================= SETTINGS =================

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "demodata") OffDemoDataBox.Text = v;
                    else if (k == "filename") OffFileNameBox.Text = v;
                    else if (k == "thumb") OffThumbBox.Text = v;
                    else if (k == "summary") OffSummaryBox.Text = v;
                    else if (k == "summarylength") OffSummaryLengthBox.Text = v;
                    else if (k == "process") ProcessBox.Text = v;
                    else if (k == "library") _library = v;
                    else if (k == "obsport") ObsPortBox.Text = v;
                    else if (k == "obspassword") ObsPasswordBox.Password = Unprotect(v);
                    else if (k == "tick") OffTickBox.Text = v;
                    else if (k == "endtick") OffEndTickBox.Text = v;
                    else if (k == "beep") BeepBox.IsChecked = v != "0";
                    else if (k == "autoend") AutoEndBox.IsChecked = v != "0";
                    else if (k == "recorder") RecorderCombo.SelectedIndex = v == "obs" ? 1 : 0;
                    else if (k == "fps") FpsBox.Text = v;
                    else if (k == "kbps") KbpsBox.Text = v;
                    else if (k == "autokbps") AutoKbpsBox.IsChecked = v != "0";
                    else if (k == "profile")
                    {
                        foreach (ComboBoxItem item in ProfileCombo.Items)
                            if (string.Equals((string)item.Tag, v, StringComparison.OrdinalIgnoreCase)) ProfileCombo.SelectedItem = item;
                    }
                    else if (k == "volumedb")
                    {
                        double db;
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out db)) VolumeSlider.Value = db;
                    }
                    else if (k == "jumptostart") JumpToStartBox.IsChecked = v != "0";
                    else if (k == "fakeactivation") FakeActivationBox.IsChecked = v == "1";
                    else if (k.StartsWith("key", StringComparison.Ordinal)) LoadHotkeySetting(k, v);
                    else if (k == "player") PlayerCombo.Text = v;
                    else if (k == "playerauto") AutoPlayerBox.IsChecked = v != "0";
                }
            }
            catch { }
        }

        private void SaveOffsetsButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            StatusText.Text = "Saved to " + SettingsPath;
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsPath, new string[]
                {
                    "process=" + ProcessBox.Text.Trim(),
                    "library=" + _library,
                    "demodata=" + OffDemoData,
                    "filename=" + OffFileName,
                    "thumb=" + OffThumb,
                    "summary=" + OffSummary,
                    "summarylength=" + OffSummaryLength,
                    "obsport=" + ParseBox(ObsPortBox, DefObsPort),
                    "obspassword=" + Protect(ObsPasswordBox.Password),
                    "tick=" + OffTick,
                    "endtick=" + OffEndTick,
                    "beep=" + (BeepBox.IsChecked == true ? "1" : "0"),
                    "autoend=" + (AutoEndBox.IsChecked == true ? "1" : "0"),
                    "recorder=" + (UseBuiltIn ? "builtin" : "obs"),
                    "fps=" + ParseBox(FpsBox, 60),
                    "kbps=" + ParseBox(KbpsBox, 7470),
                    "autokbps=" + (AutoKbpsBox.IsChecked == true ? "1" : "0"),
                    "profile=" + SelectedProfile,
                    "volumedb=" + VolumeSlider.Value.ToString(CultureInfo.InvariantCulture),
                    "jumptostart=" + (JumpToStartBox.IsChecked == true ? "1" : "0"),
                    "fakeactivation=" + (FakeActivationBox.IsChecked == true ? "1" : "0"),
                    "player=" + PlayerText,
                    "keyrecord=" + _hotVk[0], "keystart=" + _hotVk[1], "keyend=" + _hotVk[2], "keygoto=" + _hotVk[3],
                    "playerauto=" + (AutoPlayerBox.IsChecked == true ? "1" : "0")
                });
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            string warning = Recording && _active is ObsRecorder
                ? "OBS is still recording. Close anyway? OBS will keep recording."
                : Recording
                ? "Still recording. Close anyway? The recording is stopped and kept in "
                  + Path.Combine(_library, TakesFolder) + ", but not saved to a clip folder."
                : _takePath.Length > 0 || _saving
                ? "A recorded take hasn't been saved to a clip folder. Close anyway? The recording stays in "
                  + Path.Combine(_library, TakesFolder) + "."
                : null;

            if (warning != null)
            {
                var ask = MessageBox.Show(warning, "DemoRipper", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ask != MessageBoxResult.Yes) e.Cancel = true;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveSettings();
            if (_hwnd != null) foreach (int id in _hotkeys) UnregisterHotKey(_hwnd.Handle, id);
            _cancelSeek = true;
            if (_obs != null) _obs.Dispose();
            // Stops a recording cleanly so the file isn't left unreadable, or drops a prepared one.
            if (_builtIn != null) _builtIn.Dispose();
            base.OnClosed(e);
        }

        // ================= MAINTENANCE =================

        /// <summary>
        /// Shows the licences of the libraries compiled into this exe. They are MIT,
        /// which asks for the notice to travel with the binary — and the binary is all
        /// most people download, so the text is embedded rather than left in the repo.
        /// </summary>
        private void NoticesButton_Click(object sender, RoutedEventArgs e)
        {
            string text;
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("DemoRipper2026.THIRD-PARTY-NOTICES.txt"))
            {
                if (s == null) { StatusText.Text = "The notices file is missing from this build."; return; }
                using (var r = new StreamReader(s)) text = r.ReadToEnd();
            }

            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x25, 0x16)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xD6, 0xAE)),
                BorderThickness = new Thickness(0)
            };
            var save = new Button { Content = "Save a copy…", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            save.Click += (s2, e2) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "THIRD-PARTY-NOTICES.txt", Filter = "Text|*.txt" };
                if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, text);
            };

            var panel = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(save, Dock.Bottom);
            panel.Children.Add(save);
            panel.Children.Add(box);

            new Window
            {
                Title = "Third-party notices",
                Owner = this,
                Width = 700,
                Height = 560,
                Background = new SolidColorBrush(Color.FromRgb(0x15, 0x12, 0x0B)),
                Content = panel
            }.ShowDialog();
        }

        private void DiagnoseButton_Click(object sender, RoutedEventArgs e)
        {
            StringBuilder sb = new StringBuilder();
            bool attached = Mem.Attach(ProcessBox.Text.Trim());

            if (Mem.AttachedName.Length == 0)
            {
                InfoBox.Text = "No process named \"" + ProcessBox.Text.Trim() + "\" is running.";
                StatusText.Text = "Not running.";
                return;
            }

            sb.AppendLine("process     " + Mem.AttachedName + "  (pid " + Mem.ProcessId + ")");
            sb.AppendLine("path        " + Mem.ModulePath);
            sb.AppendLine("handle      " + (attached ? "opened" : "FAILED, win32 error " + Mem.OpenError));
            if (Mem.OpenError == 5)
                sb.AppendLine("            access denied — run this as administrator.");

            bool known;
            bool is32 = Mem.TargetIs32Bit(out known);
            sb.AppendLine("target      " + (known ? (is32 ? "32-bit" : "64-bit") : "unknown"));

            if (Mem.ModuleBase == IntPtr.Zero)
            {
                sb.AppendLine();
                sb.AppendLine("No module base — offsets can't be read.");
                InfoBox.Text = sb.ToString();
                return;
            }

            sb.AppendLine("base        0x" + Mem.ModuleBase.ToInt64().ToString("X8"));
            sb.AppendLine("size        " + Mem.ModuleSize);

            Mem.PeInfo pe = Mem.ReadPeHeader();
            if (pe.Valid)
            {
                sb.AppendLine("PE machine  " + pe.MachineName);
                sb.AppendLine("PE built    " + pe.BuildUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
                sb.AppendLine("            the offsets are for 2026-08-17.");
                sb.AppendLine("            a different date means run Relocate.");
            }

            sb.AppendLine();
            DumpOffset(sb, "demo data     ", OffDemoData, 8);
            DumpOffset(sb, "file name     ", OffFileName, 16);
            DumpOffset(sb, "thumbnail     ", OffThumb, 8);
            DumpOffset(sb, "summary       ", OffSummary, 8);
            DumpOffset(sb, "summary length", OffSummaryLength, 8);
            sb.AppendLine("tick          +" + OffTick.ToString().PadRight(10) + "value " + Mem.ReadInt(Mem.At(OffTick)));
            sb.AppendLine("end tick      +" + OffEndTick.ToString().PadRight(10) + "value " + Mem.ReadInt(Mem.At(OffEndTick)));

            InfoBox.Text = sb.ToString();
            StatusText.Text = "Diagnostics complete.";
        }

        private void DumpOffset(StringBuilder sb, string label, int offset, int bytes)
        {
            IntPtr a = Mem.At(offset);
            sb.AppendLine(label + "+" + offset.ToString().PadRight(10)
                          + (offset < Mem.ModuleSize ? "in-module" : "OUT-OF-RANGE"));
            sb.AppendLine("              " + Mem.HexDump(a, bytes));
            string ascii = Mem.ReadAscii(a, 24);
            if (ascii.Length > 1) sb.AppendLine("              \"" + ascii + "\"");
        }

        /// <summary>
        /// Finds the demo offsets again after a game update, using a demo you already
        /// ripped successfully as a signature. Load the same match in BO2 first. The
        /// replay clock (tick, end tick) is not among them.
        /// </summary>
        private void RelocateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!Mem.Attach(ProcessBox.Text.Trim()) || Mem.ModuleBase == IntPtr.Zero)
            { StatusText.Text = "Attach first — run Diagnose."; return; }

            // It only works on a match that is loaded in BO2 right now, so say so before the file picker.
            var ready = MessageBox.Show(
                "Relocate finds the demo offsets again after a BO2 update.\n\n"
                + "1. In BO2's theater, load a demo you already ripped with this app.\n"
                + "2. Next, pick that same demo's .demo file. Keep its .summary and .thumbnail beside it, as the app saved them.\n\n"
                + "Is that demo loaded in BO2 now?",
                "Relocate from known demo", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ready != MessageBoxResult.Yes) { StatusText.Text = "Load a demo you ripped before in BO2's theater, then Relocate again."; return; }

            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Demo (*.demo)|*.demo";
            dlg.Title = "Pick the .demo of the match loaded in BO2's theater";
            if (dlg.ShowDialog() != true) { StatusText.Text = "Cancelled."; return; }

            string demoPath = dlg.FileName;
            SetBusy(true, "Relocating…");

            Thread t = new Thread(() =>
            {
                StringBuilder sb = new StringBuilder();
                int nDemoData = 0, nFileName = 0, nThumb = 0, nSummary = 0, nSummaryLength = 0;

                try
                {
                    byte[] demo = File.ReadAllBytes(demoPath);
                    string baseName = Path.GetFileNameWithoutExtension(demoPath);
                    sb.AppendLine("Make sure THIS match is loaded in BO2.");
                    sb.AppendLine(Path.GetFileName(demoPath) + "   " + demo.Length + " bytes");
                    sb.AppendLine();

                    byte[] sig = new byte[Math.Min(32, demo.Length)];
                    Buffer.BlockCopy(demo, 0, sig, 0, sig.Length);

                    foreach (IntPtr h in Mem.Scan(sig, false, true, 10,
                        pct => Dispatcher.BeginInvoke(new Action(() => RipProgress.Value = pct / 3))))
                    {
                        foreach (IntPtr ph in Mem.Scan(
                            BitConverter.GetBytes((int)h.ToInt64()), true, true, 20, null))
                        {
                            if (Mem.ReadInt(IntPtr.Add(ph, 4)) != demo.Length) continue;
                            long off = ph.ToInt64() - Mem.ModuleBase.ToInt64();
                            sb.AppendLine("demo data        " + off);
                            if (nDemoData == 0) nDemoData = (int)off;
                        }
                    }
                    if (nDemoData == 0) sb.AppendLine("demo data        not found");

                    var thumbHits = new HashSet<IntPtr>();
                    string tPath = SidecarPath(demoPath, ".thumbnail");
                    if (tPath != null)
                    {
                        byte[] tb = File.ReadAllBytes(tPath);
                        byte[] tsig = new byte[Math.Min(32, tb.Length)];
                        Buffer.BlockCopy(tb, 0, tsig, 0, tsig.Length);
                        foreach (IntPtr h in Mem.Scan(tsig, true, true, 40, null)) thumbHits.Add(h);
                    }

                    int summaryFileLen = 0;
                    string sPath = SidecarPath(demoPath, ".summary");
                    if (sPath != null) summaryFileLen = (int)new FileInfo(sPath).Length;

                    // Entry layout: name +0, thumbnail +518, summary length +1034.
                    // A bare name hit also matches browser list entries, so the
                    // thumbnail has to line up; a matching length breaks a tie.
                    int best = -1;
                    foreach (IntPtr nh in Mem.Scan(Encoding.ASCII.GetBytes(baseName), true, true, 20, null))
                    {
                        long off = nh.ToInt64() - Mem.ModuleBase.ToInt64();
                        bool thumbOk = thumbHits.Contains(IntPtr.Add(nh, 518));
                        bool lenOk = summaryFileLen > 0
                                     && Mem.ReadInt(IntPtr.Add(nh, 1034)) == summaryFileLen;
                        int score = (thumbOk ? 2 : 0) + (lenOk ? 1 : 0);

                        if (score > best && score >= 2)
                        {
                            best = score;
                            nFileName = (int)off;
                            nThumb = (int)off + 518;
                            nSummaryLength = (int)off + 1034;
                        }
                    }

                    sb.AppendLine("file name        " + (nFileName != 0 ? nFileName.ToString() : "not found"));
                    sb.AppendLine("thumbnail        " + (nThumb != 0 ? nThumb.ToString() : "not found"));
                    sb.AppendLine("summary length   " + (nSummaryLength != 0 ? nSummaryLength.ToString() : "not found"));

                    // The picked demo's own .summary is a copy of the summary buffer, so its first
                    // bytes find the buffer directly.
                    byte[] sfile = sPath != null ? File.ReadAllBytes(sPath) : new byte[0];
                    byte[] ssig = new byte[Math.Min(64, sfile.Length)];
                    Buffer.BlockCopy(sfile, 0, ssig, 0, ssig.Length);
                    if (ssig.Length == 64 && Mem.LongestZeroRun(ssig) < 16)
                    {
                        foreach (IntPtr h in Mem.Scan(ssig, true, true, 4, null))
                        {
                            long off = h.ToInt64() - Mem.ModuleBase.ToInt64();
                            sb.AppendLine("summary          " + off + "   matches the demo's .summary");
                            if (nSummary == 0) nSummary = (int)off;
                        }
                    }

                    // No .summary matched (none beside the demo, an empty one, or not found in the game):
                    // hunt for a raw DEFLATE stream near the entry and take the first, which needs
                    // checking by hand.
                    if (nSummary == 0 && nFileName != 0)
                    {
                        Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "Hunting for the summary…"));
                        var found = Mem.HuntDeflate(Math.Max(0, nFileName - 4 * 1024 * 1024),
                            nFileName + 1024 * 1024, 16384,
                            pct => Dispatcher.BeginInvoke(new Action(() =>
                                RipProgress.Value = 33 + pct * 2 / 3)));

                        foreach (var kv in found)
                        {
                            sb.AppendLine("summary          " + kv.Key + "   inflates to " + kv.Value + "   (a guess: no .summary matched)");
                            if (nSummary == 0) nSummary = kv.Key;
                        }
                    }
                    if (nSummary == 0) sb.AppendLine("summary          not found");
                }
                catch (Exception ex) { sb.AppendLine("Failed: " + ex.Message); }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InfoBox.Text = sb.ToString();
                    if (nDemoData != 0) OffDemoDataBox.Text = nDemoData.ToString();
                    if (nFileName != 0) OffFileNameBox.Text = nFileName.ToString();
                    if (nThumb != 0) OffThumbBox.Text = nThumb.ToString();
                    if (nSummary != 0) OffSummaryBox.Text = nSummary.ToString();
                    if (nSummaryLength != 0) OffSummaryLengthBox.Text = nSummaryLength.ToString();

                    StatusText.Text = nDemoData != 0
                        ? "Relocated. Read the demo to confirm, then Save."
                        : "Could not find the demo data. Is that same demo loaded in BO2's theater? See the log.";
                    SetBusy(false, null);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>The file beside a demo, as this app writes them: name.demo.summary. Null when absent.</summary>
        private static string SidecarPath(string demoPath, string ext)
        {
            string p = demoPath + ext;
            return File.Exists(p) ? p : null;
        }
    }
}
