# BO2 Demo Ripper

Written with an External Cortex, courtesy of Anthropic.

Save Black Ops II replays as clips while you watch them, without leaving the game.

BO2 Demo Ripper runs next to **retail Steam Black Ops II**. While a replay plays
in the theater, you mark where a clip starts and ends with the number pad. The
app jumps the replay to your start point, records a preview video of just that
part, and saves the demo's files next to the video. The result is a clip folder
you can load in Redacted later for the proper cinematic render.

**Version 1.2.1**

**New in 1.2.1:** the source is published here. A new walleye on the FISH button and as the
icon. The offsets panel and the settings file use plain names. The save log says when a demo's
map or mode isn't recognised.

**New in 1.2.0:** the clip name starts with the **player's in-game name**, filled in from the
player you are watching; **several clips from one replay** recorded in one go; the start and end
come straight from the **sliders** (no tick buttons needed); one big **FISH** button records and
saves everything; a **replay timeline** showing where you are and how much of the replay
can be jumped to; **keyboard shortcuts** you can change; settings tucked under **Advanced**; a
darker walleye look.

- [Download](#download)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Hotkeys](#hotkeys)
- [Start and end](#start-and-end)
- [Replay timeline](#replay-timeline)
- [Several clips from one replay](#several-clips-from-one-replay)
- [Jumping to the start tick](#jumping-to-the-start-tick)
- [Recording](#recording)
- [What gets saved](#what-gets-saved)
- [Troubleshooting](#troubleshooting)
- [Safety](#safety)
- [About](#about)
- [Licences](#licences)
- [Building from source](#building-from-source)
- [What's in this repository](#whats-in-this-repository)

---

## Download

Get **Bo2DemoRipper2026.exe** from the [latest release](https://github.com/HarryShire/bo2-demo-ripper/releases/latest).
It's the whole app in one file, with nothing to install.

Put it in a folder you can write to, such as `Documents\BO2 Demo Ripper`, not
Program Files. The app keeps its settings in `DemoRipper.settings.txt` next to
the exe.

## Requirements

| | |
|---|---|
| Windows | **Windows 11** for recording with game sound. Windows 10 (1903 or later) works, but the built-in recorder records without sound there. Use OBS instead if you need sound on Windows 10. |
| .NET | .NET Framework 4.7.2 or later (already part of Windows 10 1803 and later). |
| Game | Retail **Steam** Black Ops II multiplayer (`t6mp.exe`). The memory offsets are for the build dated 2026-08-17. |
| Display mode | **Fullscreen windowed** (borderless) is recommended. Exclusive fullscreen can record black, and fullscreen BO2 minimises when you click another window. |
| Graphics | Any GPU with a hardware H.264 encoder (NVIDIA, AMD, Intel). Windows falls back to a slower software encoder without one. |

## Quick start

1. Start BO2, open **Theater**, and load a replay.
2. Start **BO2 Demo Ripper**. The top line should read *Found Retail BO2
   successfully*. The first time, choose the folder **clips are saved in**. Every clip gets its
   own folder inside it.
3. Fill in the **clip name**: kills and details (for example `3k`,
   `dsr knife ballista`). The **player** and the **map** fill themselves in from
   the replay: the player is whoever you are watching (untick **auto** to type
   or pick a name yourself).
4. Set where the clip starts and ends: drag the two **sliders**, or, while the
   replay plays, press **numpad `*`** at the start and **numpad `-`** at the end.
5. Press **FISH** (or numpad `/`). It pulses green when everything is in place.
   The app jumps the replay back to just before your start point and plays.
   Recording starts at the start and stops at the end.
6. The clip saves itself, and you hear a high beep.

Everything in steps 4–6 works while BO2 has focus. Beeps tell you what happened:
high for a start tick, lower for an end tick, a double beep when armed, a high
beep when a clip saves, and a double low beep when something went wrong. The
status line at the bottom of the app explains what went wrong.

## Hotkeys

These work system-wide, including while BO2 has focus. Only the number-pad keys
are used, so the `/` and `-` on the main keyboard still type normally.

| Key | What it does |
|---|---|
| **Numpad `/`** | Record the clip. If recording is armed or the replay is jumping, cancels it. If recording, stops it. If a take is waiting for a name, saves it. |
| **Numpad `*`** | **Set Start time to Current Demo Time**: the exact replay tick, and moves the Start slider there. |
| **Numpad `-`** | **Set End time to Current Demo Time**: the exact replay tick, and moves the End slider there. |
| **Numpad `+`** | **Go to the start time**: jump the replay there and pause on it. Press again to stop. |

Change any of them under *Advanced* → *Offsets and maintenance* → **Keyboard shortcuts**: click a
box and press the key you want (**Numpad defaults** puts them back). With more than
one clip queued, numpad `*` and `-` set the **newest** clip's start and end.

## Start and end

The **sliders** are the clip's start and end: their times are what the app jumps
to and records. A *tick* is BO2's replay clock in milliseconds; the app shows the
live time and tick under the sliders, e.g. `replay 2:37 / 4:39 tick 159000 / 280900`.

- Numpad `*` and `-` set an **exact** tick (to the millisecond) and move the slider
  there. Under the sliders it says `(tick 159000)` for an exact tick and
  `(from the slider)` otherwise.
- An end tick before the start tick (or the other way round) clears the other
  one, and the status line says so.
- Moving a slider or typing a time by hand goes back to the slider's time.
- Exact ticks belong to the replay they were set on, and aren't used on any other
  replay.
- **Auto End Recording at End Tick** (on by default, under *Advanced*) stops
  recording when the replay plays past the end tick.

## Replay timeline

Above the sliders, a bar shows the whole replay, just to look at:

- the **playhead** and the current time above it;
- a gold strip for each clip, from its start to its end;
- small notches where BO2 has laid its **jump points** (one every 10 seconds, as far as the
  replay has been played);
- a **hatched** part where there are no jump points yet. Hover it for a reminder: play through
  that part once and you can jump to it. The app only knows what it has seen play, so a part
  you watched before opening it shows as hatched until it plays again.

The progress bar under FISH fills while clips record, across all of them when several are
queued.

## Several clips from one replay

When clip 1 has a name and a start and end, **Queue for Recording** (under the
last clip) adds clip 2, with its own kills, details and sliders; then clip 3, up
to 5. **FISH** then records them all in one go: it jumps to clip 1, records it,
jumps to clip 2, records it, and so on, and saves everything in **one folder**
with the replay. Each video is named after its own clip. **✕** removes a queued
clip.

## Jumping to the start tick

BO2's theater lets you jump through a replay in 10-second steps: **Left** jumps
back, **Right** jumps forward, and **Space** plays or pauses. BO2 Demo Ripper
presses these keys for you, even while another window has focus.

- **Jump to Start Tick before recording** (on by default, under *Advanced*): when you press
  FISH with a start set, the app jumps to the last jump point before the
  start tick, from ahead of it or behind it. It then arms recording and presses
  play. Recording starts the moment the replay reaches the start tick.
- **Go to** (numpad `+`) jumps, plays, and pauses right on the start tick.
- **First watch of a replay:** BO2 only creates jump points as far as the replay
  has been watched. If the start tick is past that point, the app jumps as far
  as it can and plays from there. It still starts recording at the start tick,
  just after a longer wait, and the status line says why.
- The keys have to be BO2's defaults (Left, Right, Space).
- If jumps do nothing on your PC, tick **Wake BO2 before each theater key**
  under *Advanced* → *Offsets and maintenance*.

## Recording

Open *Advanced* → **Recording** to choose the recorder and its settings. Settings
are remembered.

### Built-in recorder (default)

Records the BO2 window straight to an MP4, with **only BO2's sound**. Nothing
else needs to be running, and Discord, music and notification sounds stay out
of the recording.

| Setting | Default | Notes |
|---|---|---|
| Frame rate | 60 | The file is a steady frame rate whatever BO2 is drawing. |
| H.264 profile | **Main** | Main plays everywhere. High looks a little better at the same bitrate. Baseline is the simplest and least efficient. |
| Bitrate | **Auto** | 7470 kbps up to 1080p, 12000 at 1440p, 20000 at 4K. The note under it shows BO2's size and the bitrate it gets. Untick Auto to type your own. |
| Game volume | 0 dB | Makes BO2 louder or quieter **in the recording only**. Loud moments are softened instead of distorting. Changes take effect immediately. |

Always: resolution = BO2's own size, a keyframe every 0.5 s (quick scrubbing in
editors), and AAC audio at 48 kHz, 192 kbps.

The recorder gets ready when you press FISH, so recording begins within
milliseconds of the start tick. If BO2 closes while recording, the file is still
finished properly.

### OBS

Pick **OBS** to record through OBS Studio instead. In OBS, open **Tools →
WebSocket Server Settings**, tick **Enable WebSocket server**, then use **Show
Connect Info** and copy the port and password into the app. **Test connection**
checks it. OBS records with its own settings, and the file is moved into the
clip folder when OBS has finished writing it. The password is stored encrypted
for your Windows account.

## What gets saved

Each clip is a folder, in the folder you chose for clips, named
`<player> - <map> - <kills> <details> <start> - <end>`, for example
`Mycelium - express - 3k dsr knife ballista 243 - 304` for 2:43 to 3:04.
More clips from the same replay add ` - <kills> <details> <start> - <end>` each:
`Mycelium - raid - 2k dsr noice ns 700 - 715 - 3k scar 810 - 825`.

```
Mycelium - express - 3k dsr knife ballista 243 - 304\
    tdm_mp_express_….demo              the replay
    tdm_mp_express_….demo.summary
    tdm_mp_express_….demo.tags
    tdm_mp_express_….demo.thumbnail
    Mycelium - express - 3k dsr knife ballista 243 - 304.mp4    the preview
    clip.txt
```

The four demo files are what Redacted needs to load the replay. The demo is read
from BO2's memory when you press FISH, so it doesn't matter what BO2 does
afterwards.

`clip.txt` lists the clip's details, one `key=value` per line:

| Key | Meaning |
|---|---|
| `clip` | folder name |
| `player` | the player's in-game name |
| `demo`, `map`, `mode`, `size` | the replay file, map id (e.g. `mp_express`), game mode, demo size in bytes |
| `ripped`, `source` | when it was saved, and `retail t6mp` |
| `start`, `end` | clip times as shown in the game (`2:43`) |
| `start_tick`, `end_tick`, `demo_end_tick` | exact replay ticks, when set |
| `preview` | the preview video's file name, when recorded |
| `clips`, `part2`, `part2_start`, `part2_end`, `part2_start_tick`, `part2_end_tick`, `part2_preview` … | with several clips: how many, and each extra clip's name, times, ticks and video |

**Takes.** A recording goes to `<your clips folder>\_takes\` first. It moves into the clip
folder when it's saved. If the clip has no name, or a folder with that name
already exists, the take waits and the FISH button reads **Save clip**.
**Discard take** deletes a waiting take. **Rip demo only** (under *Advanced* →
*Offsets and maintenance*) saves the demo files with no preview.

## Troubleshooting

**"Not connected"** — retail BO2 (`t6mp.exe`) isn't running.

**Nothing reads right after a game update** — open *Advanced* → *Offsets and maintenance* →
**Diagnose** to see what the app finds, including the game's build date. Then **Relocate from
known demo…** finds the demo offsets again:

1. In BO2's theater, load a demo **you already ripped with this app**. It has to be the match
   that is loaded in the game right now.
2. Press **Relocate from known demo…** and pick that same demo's `.demo` file, with the
   `.summary` and `.thumbnail` the app saved beside it.
3. Check the numbers it fills in, then press **Save**.

The replay clock offsets (Tick and End tick) have to be found again by hand. The whole story,
with what a good result looks like: [docs/relocate.md](docs/relocate.md).

**"No replay tick to mark"** — a replay has to be open in the theater.

**BO2 doesn't list 4K (or your monitor's resolution)**
- If Windows is **duplicating** your screen to another monitor, BO2 only sees
  resolutions both screens support. Set the displays to **Extend** (Settings →
  System → Display).
- With display scaling above 100%, Windows reports a smaller screen to BO2. Fix:
  right-click `t6mp.exe` → Properties → Compatibility → Change high DPI settings
  → tick *Override high DPI scaling behavior* → **Application**.

**"BO2 is minimised, so there's no picture to record"** — fullscreen BO2
minimises when you click another window. Press numpad `/` from inside the game,
or play in fullscreen windowed.

**Recording is silent** — check BO2's own volume first: the recording has only
BO2's sound, at the level BO2 plays it. Raise **Game volume** if you play
quietly. On Windows 10 the built-in recorder can't capture one program's sound;
the status line says so when recording starts.

**Jumping to the start tick doesn't work**
- The theater keys have to be BO2's defaults (Left, Right, Space).
- On a replay's first watch, jump points only exist as far as you've watched.
- Try **Wake BO2 before each theater key** under *Advanced* → *Offsets and maintenance*.
- If it still fails, the app arms anyway: jump back yourself and press play.

**"Numpad / already taken by another program"** — another app has registered
that key. Close it, or pick other keys under *Advanced* → *Offsets and maintenance* →
**Keyboard shortcuts**.

## Safety

- **The app never changes BO2's memory.** It only *reads* it: the replay, the
  replay clock, the map, and the players' names with the one you are watching.
  Jumping uses the same key presses you'd make yourself, sent to BO2's window;
  with *Wake BO2 before each theater key* ticked, it also tells the window it
  has been activated around each key.
- Retail BO2 uses **Valve Anti-Cheat**. **Use it at your own risk.**
- Use it in the theater, not in online matches.

This project isn't affiliated with Activision, Treyarch or Valve.

## About

BO2 Demo Ripper picks up where Airyz's demo ripper left off, after the
2026-08-17 retail update moved the demo offsets. It is tested by hand in the game.

## Licences

BO2 Demo Ripper is **MIT licensed** - see [LICENSE](LICENSE). Use it, change it,
ship it; keep the copyright notice with it.

It is one file with its libraries compiled in, so their licence notices travel
with it. **THIRD-PARTY-NOTICES.txt** lists them in full - SharpDX,
Costura and Fody, all MIT, the Windows SDK reference assemblies used to build it,
and the two Microsoft samples (MIT) that its window and sound capture follow - and
the same text is inside the app, under *Offsets
and maintenance* -> **Third-party notices**, with a *Save a copy* button. The
file is also published beside the exe on every release.

The app ships no part of OBS Studio or Black Ops II, and is not affiliated with
Activision, Treyarch or Valve.

## Building from source

Visual Studio 2022 (17.13 or later, for `.slnx` solutions) or Visual Studio 2026,
with the **.NET desktop development** workload.

1. Open `DemoRipper2026\DemoRipper2026.slnx`.
2. Build the **Release** configuration. NuGet packages restore automatically.
3. The app is `DemoRipper2026\DemoRipper2026\bin\Release\Bo2DemoRipper2026.exe`.

From the command line:

```
msbuild DemoRipper2026\DemoRipper2026\DemoRipper2026.csproj /restore /p:Configuration=Release
```

.NET Framework 4.7.2, x86 (BO2 is a 32-bit game). Packages:
`Microsoft.Windows.SDK.Contracts` (window capture), `SharpDX.Direct3D11` and
`SharpDX.MediaFoundation` (video encoding), and `Costura.Fody`, which packs the
SharpDX DLLs into the exe at build time.

## What's in this repository

| | What it is |
|---|---|
| `DemoRipper2026\` | The source of BO2 Demo Ripper, the app described above. |
| `docs\relocate.md` | How Relocate finds the demo again after a game update. |
| `LICENSE` | MIT. |
| `THIRD-PARTY-NOTICES.txt` | The licences of the libraries compiled into the exe. |

The built app is on the [Releases](https://github.com/HarryShire/bo2-demo-ripper/releases) page.
