# Relocate from known demo

After a Black Ops II update, the game's memory can move around, and BO2 Demo Ripper stops finding
the replay: **Read demo** comes back empty, or **Diagnose** shows a different build date than the
one the app was made for. **Relocate from known demo…** finds the demo's places in memory again.

## The one rule

**Use a demo you already ripped with this app, and have that same match loaded in BO2's theater.**

- It has to be **in your theater**, so you can load it in BO2: one of your own saved or recent films.
- It has to have been **saved with BO2 Demo Ripper before**: its clip folder holds the `.demo`, and
  beside it the `.demo.summary` and `.demo.thumbnail` the app wrote. Relocate reads those three.
- It has to be **loaded in the theater right now**, while Relocate runs.

Relocate recognises the match by comparing what is in BO2's memory with those files. A demo you
never ripped has no files to compare with, and a ripped demo that isn't loaded isn't in memory.
Either way it finds nothing.

## Step by step

1. Start retail BO2, open **Theater**, and load a demo you ripped before with this app.
2. In BO2 Demo Ripper, open **Advanced** → **Offsets and maintenance**.
3. Optional: press **Diagnose**. It shows the game's build date next to the one the offsets are for.
4. Press **Relocate from known demo…**. It asks whether that demo is loaded in BO2 now: answer **Yes**.
5. Pick **that same demo's** `.demo` file, in its clip folder, with its `.demo.summary` and
   `.demo.thumbnail` beside it.
6. Read the log. The numbers it finds are filled into the boxes above.
7. If they look right, press **Save**. They are kept in `DemoRipper.settings.txt` next to the exe.
   If a number looks wrong, fix it in its box first: the app also saves the boxes when it closes.

## What it finds, and how

| Box | What it is | How Relocate finds it |
|---|---|---|
| **Demo data** | where the game keeps a pointer to the loaded demo, then its size | the first 32 bytes of your `.demo` are searched for in the game's memory; the place in the game that points at them and is followed by the file's exact size |
| **File name** | the loaded demo's file name | the file name in the game's memory where the thumbnail and the summary length line up as below |
| **Thumbnail** | the demo's thumbnail picture | 518 bytes after the file name; it has to match the start of your `.demo.thumbnail` |
| **Summary length** | how long the demo's summary is | 1,034 bytes after the file name; if it equals your `.demo.summary`'s size, that settles a tie |
| **Summary** | the demo's summary | the first 64 bytes of your `.demo.summary` are searched for in the game |

If no `.demo.summary` matches (it is missing, empty, or not this match's), Relocate guesses the
summary instead (the first compressed stream near the file name) and says so in the log. Check that
number by hand.

**Tick** and **End tick**, the replay clock, are not found by Relocate. They have to be found again
by hand after an update.

## What success looks like

For the build the app was made for (2026-08-17), the log reads:

```
demo data        47344080
file name        44940850
thumbnail        44941368
summary length   44941884
summary          44589248   matches the demo's .summary
```

After an update the numbers will be different. What matters is that every line has a number and
the summary line ends with `matches the demo's .summary`.

## When it doesn't work

| The log says | What to do |
|---|---|
| `demo data   not found` | The match loaded in BO2 isn't the demo you picked, or the file isn't the one this app saved. Load the right demo, or pick its `.demo`. |
| `file name   not found` | The `.demo.thumbnail` is missing, or it isn't the one saved with this demo. |
| the summary line ends with `(a guess: no .summary matched)` | The `.demo.summary` is missing, empty, or isn't this match's. Check the number before you press Save. |
| `summary   not found` | No `.demo.summary` matched and the guess found nothing either (it needs the file name). Load the right demo first. |

The boxes are saved when you press **Save** and again when the app closes, so correct a wrong
number before you close it. The 2026-08-17 numbers above are the built-in ones, if you need to
type them back.
