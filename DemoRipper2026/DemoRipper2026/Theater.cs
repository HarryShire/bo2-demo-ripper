using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DemoRipper2026
{
    public enum TheaterKey { JumpBack, JumpForward, PlayPause }

    /// <summary>
    /// Presses BO2's theater keys by posting key messages to its window. Nothing
    /// is written to the game's memory, and focus never changes, so this works
    /// while another window is active. On retail: Right +10000, Left -10000,
    /// Space play and pause, all with BO2 in the background.
    /// https://learn.microsoft.com/windows/win32/inputdev/wm-keydown
    /// </summary>
    public sealed class TheaterKeys
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                        uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        private const uint WM_ACTIVATE = 0x0006, WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008,
                           WM_ACTIVATEAPP = 0x001C, WM_NCACTIVATE = 0x0086, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        private const uint SMTO_ABORTIFHUNG = 0x2;

        public const uint VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_SPACE = 0x20;

        private const int HoldMs = 50;

        private readonly IntPtr _window;

        public uint JumpBackKey = VK_LEFT;
        public uint JumpForwardKey = VK_RIGHT;
        public uint PlayPauseKey = VK_SPACE;

        /// <summary>
        /// Also tell BO2 it was activated around each key, then deactivated, for
        /// setups where the game ignores keys while in the background. Skipped when
        /// BO2 really has focus.
        /// </summary>
        public bool FakeActivation;

        public TheaterKeys(IntPtr window)
        {
            _window = window;
        }

        /// <summary>The main window of a running process, or zero.</summary>
        public static IntPtr FindWindow(int processId)
        {
            try { return Process.GetProcessById(processId).MainWindowHandle; }
            catch { return IntPtr.Zero; }
        }

        /// <summary>Key down, a short hold, key up. The game acts on the key down.</summary>
        public void Press(TheaterKey key)
        {
            uint vk = key == TheaterKey.JumpBack ? JumpBackKey
                    : key == TheaterKey.JumpForward ? JumpForwardKey
                    : PlayPauseKey;

            bool wake = FakeActivation && GetForegroundWindow() != _window;
            if (wake)
            {
                Send(WM_ACTIVATEAPP, 1);
                Send(WM_NCACTIVATE, 1);
                Send(WM_ACTIVATE, 1);   // WA_ACTIVE
                Send(WM_SETFOCUS, 0);
            }

            // lParam: repeat count 1, scan code, extended-key bit for the arrow
            // and navigation keys, and for key up the previous-state and transition bits.
            uint scan = MapVirtualKey(vk, 0);
            bool extended = vk >= 0x21 && vk <= 0x2E;
            uint down = 1u | (scan << 16) | (extended ? 1u << 24 : 0u);
            uint up = down | (1u << 30) | (1u << 31);

            if (!PostMessage(_window, WM_KEYDOWN, new IntPtr((int)vk), new IntPtr(unchecked((int)down))))
                throw new InvalidOperationException("Couldn't send a key to BO2 (win32 error " + Marshal.GetLastWin32Error() + ").");
            Thread.Sleep(HoldMs);
            PostMessage(_window, WM_KEYUP, new IntPtr((int)vk), new IntPtr(unchecked((int)up)));

            if (wake)
            {
                Thread.Sleep(20);
                Send(WM_KILLFOCUS, 0);
                Send(WM_ACTIVATE, 0);   // WA_INACTIVE
                Send(WM_NCACTIVATE, 0);
                Send(WM_ACTIVATEAPP, 0);
            }
        }

        private void Send(uint msg, int wParam)
        {
            IntPtr result;
            SendMessageTimeout(_window, msg, new IntPtr(wParam), IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out result);
        }
    }

    public sealed class SeekCancelledException : Exception
    {
        public SeekCancelledException() : base("Cancelled.") { }
    }

    /// <summary>
    /// Moves a paused replay to the jump point at or before a start tick, from
    /// either side of it, using only the theater keys and the tick it reads back.
    ///
    /// Retail jump points sit every 10 s at tick 2000 + 10000·n, from 12000 to the
    /// last one before the end. The grid only decides whether one more jump
    /// forward would pass the start tick; every move is confirmed by reading the
    /// tick, so a different grid still converges.
    ///
    /// No Windows calls in here: the tick reader, key press, clock and sleep are
    /// passed in.
    /// </summary>
    public sealed class TheaterSeek
    {
        public const int GridOrigin = 2000;
        public const int GridSpacing = 10000;
        public const int SnapTolerance = 5;        // a landing can read a few ms short, e.g. 21997 for 22000

        private const int MaxPresses = 150;        // a 12-minute replay has 72 jump points
        private const int JumpTimeoutMs = 1500;
        private const int PlayTimeoutMs = 1500;
        private const int SettleMs = 40;

        private readonly Func<int> _readTick;
        private readonly Action<TheaterKey> _press;
        private readonly Func<long> _nowMs;
        private readonly Action<int> _sleep;
        private readonly Func<bool> _cancelled;

        public readonly StringBuilder Log = new StringBuilder();
        public int Presses { get; private set; }

        public TheaterSeek(Func<int> readTick, Action<TheaterKey> press, Func<long> nowMs,
                           Action<int> sleep, Func<bool> cancelled)
        {
            _readTick = readTick;
            _press = press;
            _nowMs = nowMs;
            _sleep = sleep;
            _cancelled = cancelled ?? (() => false);
        }

        public struct Result
        {
            public bool Ok;
            public int Tick;
            public string Message;
        }

        /// <summary>The grid jump point at or before a tick, allowing for landings a few ms short.</summary>
        public static int GridAtOrBefore(int tick)
        {
            int n = (int)Math.Floor((tick + SnapTolerance - GridOrigin) / (double)GridSpacing);
            return GridOrigin + n * GridSpacing;
        }

        /// <summary>
        /// Pauses if playing, then jumps until the replay sits on the jump point at
        /// or before startTick. Leaves it paused.
        /// </summary>
        public Result ToJumpPointBefore(int startTick, int endTick)
        {
            int tick = Read();
            Note("start tick " + startTick + ", replay at " + tick);

            if (IsPlaying())
            {
                tick = Pause();
                Note("paused at " + tick);
            }

            bool overshot = false, moved = false;
            while (true)
            {
                if (Presses >= MaxPresses)
                    return Fail(tick, "Gave up after " + Presses + " jumps without reaching the start tick.");

                if (tick > startTick)
                {
                    int landed = Jump(TheaterKey.JumpBack, tick);
                    if (landed == tick)
                    {
                        return Fail(tick, !moved
                            ? "BO2 didn't jump back. Either the theater keys aren't reaching it, or this replay hasn't been watched "
                              + "far enough for a jump point before here yet."
                            : "Can't jump back any further: the first jump point (tick " + tick + ") is already past the start tick. "
                              + "Restart the replay from the beginning.");
                    }
                    tick = landed;
                    moved = true;
                    continue;
                }

                // At or before the start tick. One more jump forward, unless it
                // would pass the start tick or a jump forward already did.
                int next = GridAtOrBefore(tick) + GridSpacing;
                if (overshot || next > startTick || (endTick > 0 && next >= endTick))
                    return Done(tick, startTick);

                // BO2 only has jump points as far as the replay has been watched,
                // so on a first watch a jump forward can go nowhere. Play from here.
                int after = Jump(TheaterKey.JumpForward, tick);
                if (after == tick)
                {
                    Result short_ = Done(tick, startTick);
                    short_.Message = "No jump point further forward yet (BO2 adds them as a replay is first watched), so it plays from here.";
                    return short_;
                }
                if (after > startTick)
                {
                    Note("jump forward passed the start tick, going back one");
                    overshot = true;
                }
                tick = after;
                moved = true;
            }
        }

        /// <summary>
        /// Plays from a paused position and pauses as close to startTick as it can,
        /// aiming slightly before it so recording armed there still sees the replay
        /// cross the start tick. leadMs covers the delay between key and pause.
        /// </summary>
        public Result PlayAndPauseAt(int startTick, int leadMs)
        {
            int tick = Read();
            if (tick > startTick) return Fail(tick, "The replay is already past the start tick.");

            _press(TheaterKey.PlayPause);
            Presses++;
            long started = _nowMs();
            int from = tick;

            while (true)
            {
                if (_cancelled()) throw new SeekCancelledException();
                tick = Read();
                if (tick >= startTick - leadMs) break;

                long waited = _nowMs() - started;
                if (tick == from && waited > PlayTimeoutMs)
                    return Fail(tick, "BO2 didn't start playing. Is the replay open, and are the theater keys the defaults?");
                // Normal speed is about 1 ms of replay per ms; allow slow motion to 1/8.
                if (waited > (startTick - from) * 8L + 5000)
                    return Fail(tick, "The replay didn't reach the start tick in time.");
                _sleep(1);
            }

            _press(TheaterKey.PlayPause);
            Presses++;
            int landed = WaitStill();
            Note("paused at " + landed + " (" + (landed - startTick).ToString("+#;-#;0") + " ms from the start tick, lead " + leadMs + ")");
            return new Result { Ok = true, Tick = landed, Message = "" };
        }

        /// <summary>Presses play. True once the tick is seen moving.</summary>
        public bool Play()
        {
            int from = Read();
            _press(TheaterKey.PlayPause);
            Presses++;
            long started = _nowMs();
            while (_nowMs() - started < PlayTimeoutMs)
            {
                if (_cancelled()) throw new SeekCancelledException();
                if (Read() != from) return true;
                _sleep(5);
            }
            return false;
        }

        private int Read()
        {
            if (_cancelled()) throw new SeekCancelledException();
            int tick = _readTick();
            if (tick <= 0) throw new InvalidOperationException("Lost the replay tick. Is the replay still open?");
            return tick;
        }

        private bool IsPlaying()
        {
            int a = Read();
            _sleep(80);
            return Read() != a;
        }

        private int Pause()
        {
            _press(TheaterKey.PlayPause);
            Presses++;
            int tick = WaitStill();
            if (IsPlaying()) throw new InvalidOperationException("BO2 didn't pause. Is the replay open, and are the theater keys the defaults?");
            return tick;
        }

        /// <summary>Waits for the tick to hold still over a short window, then returns it.</summary>
        private int WaitStill()
        {
            long started = _nowMs();
            int last = Read();
            while (_nowMs() - started < JumpTimeoutMs)
            {
                _sleep(SettleMs);
                int now = Read();
                if (now == last) return now;
                last = now;
            }
            return last;
        }

        /// <summary>Presses a jump key and returns where the replay settled, or the old tick if it didn't move.</summary>
        private int Jump(TheaterKey key, int from)
        {
            _press(key);
            Presses++;
            long started = _nowMs();
            while (_nowMs() - started < JumpTimeoutMs)
            {
                // Paused, so any real move counts: jumping back from just past a
                // jump point moves only as far as that point.
                int tick = Read();
                if (Math.Abs(tick - from) > SnapTolerance)
                {
                    // Landings settle a few ms after the jump (192000, then 191999).
                    _sleep(SettleMs);
                    tick = Read();
                    Note((key == TheaterKey.JumpBack ? "back    " : "forward ") + from + " -> " + tick);
                    return tick;
                }
                _sleep(5);
            }
            Note((key == TheaterKey.JumpBack ? "back    " : "forward ") + from + " -> no change");
            return from;
        }

        private Result Done(int tick, int startTick)
        {
            Note("on " + tick + ", " + (startTick - tick) + " ms before the start tick, " + Presses + " presses");
            return new Result { Ok = true, Tick = tick, Message = "" };
        }

        private Result Fail(int tick, string message)
        {
            Note("failed: " + message);
            return new Result { Ok = false, Tick = tick, Message = message };
        }

        private void Note(string line)
        {
            Log.AppendLine(line);
        }
    }
}
