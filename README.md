# Video Timecode

Adds a running, **frame-accurate time-of-day timecode** and other text overlays to your
videos, using the recording date the camera embedded in the file. Drop videos on the app,
pick a style, and click Run.

![Example frame](docs/example.png)

![Options window](docs/screenshot.png)

## Features

- **Per-frame, sub-second labels** such as `2026-10-25 15:48:14.033`. Each frame gets its own value.
- **Any number of text overlays**, in any of 9 positions:
  - **Each row has its own** font, size, color and edge (outline, box behind text, shadow or none).
  - **Rows at the same position stack** in list order, and can line up by their left edges, centers or right edges.
  - **Type `\n` for a new line**, in the text or in a date format.
  - **Font warnings:** an orange Font cell means either the font's digits vary in width (changing numbers wobble), or it reserves very tall lines, so its letters come out tiny (see [Fonts](#fonts)).
- **Pictures** (logos, banners): drop a PNG, JPG, BMP, GIF or TIFF onto the preview and it goes in the position you drop it on. Dropping it on the overlay table or the window, or using *Add overlay → Picture...*, also works. A picture row stacks and lines up with text rows, *Size %* sets its height, and transparency is kept. Double-click its row to swap the file. Pictures can only be **burned in**: subtitle files can't hold them.
- **Arrange in the preview:**
  - **Click** an overlay to select it; its table row is highlighted.
  - **Drag** it to another position. Drop in the upper or lower half of an area to put it above or below the rows already there.
  - **Scroll the mouse wheel** to make the selected overlay bigger or smaller.

  The Position menu and Size column in the table work too.
- **Never overwrites anything:** if an output name is taken, the next free one is used (see [Output files](#output-files)).

  Fill the text with tokens:

  | Token | Shows |
  |---|---|
  | `{datetime}` `{date}` `{time}` | recording date/time; add a custom format like `{time:hh:mm:ss tt}` |
  | `{elapsed}` | time since the clip started |
  | `{frame}` `{frames}` | current frame number / total frames |
  | `{tc}` | camera timecode (SMPTE, drop-frame aware) |
  | `{filename}` `{camera}` `{gps}` | file name, camera make/model, GPS location (when recorded) |
  | `{resolution}` `{fps}` `{codec}` `{tz}` | video details, UTC offset |

  Anything else you type is shown as-is (static text).
- **Three output types:**
  - an external `.ass` subtitle file (the video is untouched)
  - a subtitle stream in a new `.mkv` (no quality loss)
  - burned into the picture
- **Burn-in matches the source:**
  - same codec (H.264, HEVC, AV1, ProRes) and bitrate
  - same bit depth and chroma (8/10-bit, 4:2:0/4:2:2)
  - same color tags, including HDR10/HLG and HDR mastering metadata
  - audio is always copied untouched, as are metadata, chapters and camera timecode
- **Uses your GPUs automatically on any PC.** It tests every NVIDIA (NVENC), AMD (AMF) and Intel (Quick Sync) encoder present, checking the real output format, and runs several videos at once across them. If a GPU can't handle a file, that file is retried on another GPU, then on the CPU.
- **Daylight saving time handled** for the recording date, frame by frame across a clock change.
- **Presets:** save any number of named setups, switch between them from the top of the window, and give any preset its own drag-and-drop shortcut. The last run's settings are always available too.

## Download

1. Download `video-timecode-vX.Y.Z.zip` from the [latest release](../../releases/latest) and extract it anywhere.
2. Drag videos (or a folder) onto **`VideoTimecode.exe`**, or open it and add them in the window.

The first time it runs, it offers to download FFmpeg (about 85 MB, from
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)). The download is checked
against the published SHA-256 checksum and stored in `%LOCALAPPDATA%\VideoTimecode`. If
FFmpeg is already on your PATH, or next to the exe, that copy is used instead.

Requires 64-bit Windows 10 or 11. Nothing else needs installing.

| Drop videos on | What happens |
|---|---|
| `VideoTimecode.exe` | Opens the window. Pick options, click **Run**, and your choices are saved. |
| `Video Timecode - use last settings.bat` | Starts processing right away with the last run's settings |
| `Video Timecode - <preset>.bat` | Starts processing right away with that preset. Create these from **More** in the window. |

## Presets

The **Preset** bar at the top of the window holds your saved setups. A preset stores every
choice in the window: output type, encoding options, overlays, style and time settings.

| Entry | What it is |
|---|---|
| **Last run** | The settings used by the most recent Run, saved automatically. This is what the window opens with. |
| **Defaults** | The factory settings |
| *Your presets* | Saved with **Save as...**. **Save** updates the selected preset with the current settings. |

Choosing an entry loads its settings into the window. **More** has *Rename*, *Delete*, *Open
presets folder*, and **Create drag-and-drop shortcut**. That last option writes
`Video Timecode - <preset>.bat` next to `VideoTimecode.exe`. Drop videos on it to process them
with that preset straight away, without opening the options.

### Where settings are stored

Everything lives in `%LOCALAPPDATA%\VideoTimecode` (for example
`C:\Users\<you>\AppData\Local\VideoTimecode`):

| File | Contents |
|---|---|
| `settings.json` | Last run's settings, written each time you click Run |
| `presets\<name>.json` | One file per preset. Copy these to another PC to share presets. |
| `encoders.json` | Which GPU encoders this PC has, with every test result (re-checked when the GPUs, their drivers, FFmpeg or its Windows *Graphics settings* preference change) |
| `last-run.log`, `error.log` | Details of the last run, and of any unexpected errors |
| `diagnostics.txt` | The last report from **Diagnostics...**, to send when asking for help |
| `ffmpeg\` | FFmpeg, if the app downloaded it |

## Date and time format

*Label format* (used by `{datetime}`) and custom formats inside tokens, like
`{datetime:h:mm tt}`, `{date:dddd}` or `{time:HH:mm:ss}`, use these codes. **Format
help...** next to Label format shows them with live examples.

| Code | Shows | Example |
|---|---|---|
| `yyyy` / `yy` | Year | 2026 / 26 |
| `MMMM` / `MMM` / `MM` / `M` | Month: name / short name / 2 digits / number | October / Oct / 10 / 10 |
| `dddd` / `ddd` | Day of the week / short | Monday / Mon |
| `dd` / `d` | Day of the month, 2 digits / number | 05 / 5 |
| `hh` / `h` | Hour, 12-hour clock, 2 digits / number | 05 / 5 |
| `HH` / `H` | Hour, 24-hour clock, 2 digits / number | 17 / 17 |
| `mm` / `m` | Minutes, 2 digits / number | 03 / 3 |
| `ss` / `s` | Seconds, 2 digits / number | 09 / 9 |
| `f` `ff` `fff` | Tenths / hundredths / milliseconds (up to `fffffff`) | 1 / 12 / 123 |
| `F` `FF` `FFF` | Same, trailing zeros dropped | 12 instead of 120 |
| `tt` / `t` | AM/PM / A/P | PM / P |
| `zzz` / `zz` / `z` | UTC offset | -07:00 / -07 / -7 |
| `g` | Era | A.D. |
| `\n` | New line | |
| `'text'` | Text shown as typed | `'at'` |
| `\x` | One character shown as typed | `\h` shows h |

Letters are case-sensitive (`MM` = month, `mm` = minutes). Spaces and `: / - , .` appear as
typed. Month and day names are in English.

For example, `h:mm tt\ndddd, MMMM d, yyyy` gives

```
5:23 PM
Friday, October 23, 2026
```

## Fonts

Each overlay can use its own font. **Avoid math and some symbol fonts such as Cambria
Math.** They reserve very tall lines for big symbols, and subtitle renderers size text by that
line height, so the letters come out at a fraction of the chosen size: Cambria Math renders
at about 18%. The app flags these fonts in orange, leaves them out of the quick font list,
and the preview shows them at their real, small size.

## Output files

Outputs are written next to each video. Your originals are never changed.

| Output type | Creates |
|---|---|
| External subtitle file | `clip.ass`. VLC and mpv load it automatically with `clip.mp4`. |
| Subtitle stream | `clip_timecode.mkv` (streams copied; turn subtitles on in the player) |
| Burn into picture | `clip_timecode_burned.mp4` / `.mov` / `.mkv` (same container type as the source where possible) |

**Existing files are never overwritten.** If a name is already taken (or already claimed by
another video in the same batch), the next free one is used:

- **Videos:** `clip_timecode_burned (2).mp4`, `(3)`, ...
- **Subtitle files:** `clip.2.ass`, `clip.3.ass`, ... This form keeps players loading them automatically with `clip.mp4`. If an older `clip.ass` is also there, a player may show both, so delete the one you don't want.

## Burn-in encoding

With the defaults (**Same as source**, **Match source bitrate**, **Automatic** encoder), each
video is re-encoded as close to the original as possible:

- **Codec:** H.264 stays H.264, HEVC stays HEVC, AV1 stays AV1, ProRes stays ProRes (same profile). Older codecs such as MPEG-2 become H.264, or HEVC for 10-bit and 4:2:2 sources.
- **Bitrate** matches the source's video bitrate. Choose *Higher* (1.5x) or *Smaller file* (0.6x) to trade size for quality.
- **Format:** bit depth, chroma subsampling and color/HDR tags are kept. If no GPU can encode the format (for example 10-bit 4:2:2 on many GPUs), that video goes to the CPU instead of being downgraded.
- **Everything else is copied:** audio tracks, metadata including the recording date (so you can run the tool on its own output), chapters and camera timecode.

### GPUs and parallel jobs

One encode at a time leaves a GPU mostly idle, because the work around the encoder (decoding,
drawing the text) is the bottleneck. Running several at once fixes that. Measured on 30-second 4K clips:

| Setup | Seconds per clip |
|---|---|
| CPU only (x264) | 29.5 |
| 1 job on NVIDIA NVENC | 11.6 |
| 3 jobs at once on NVENC | **4.0** |
| NVENC + CPU together | slower than NVENC alone |

So **Automatic** runs up to 4 videos at once, depending on how many CPU cores the PC has,
spread across all GPU encoders. The CPU is used only when no GPU can produce the format.

To choose yourself, untick *Automatic* and tick one or more encoders. The batch is then
shared between just those, and the CPU counts as an extra encoder if ticked. A file goes only
to the encoders you ticked: if none of them can handle it (for example 10-bit 4:2:2 on a GPU
without support for it), the file is marked as failed, with a suggestion to tick another
encoder, instead of quietly using one you didn't pick. *Parallel jobs* sets how many videos
run at once.

PCs with several GPUs get one entry per GPU, each tested and used separately (identical
cards are numbered, e.g. *#1* and *#2*). AMD and Intel GPUs are always opened by number, so
an AMD GPU works alongside NVIDIA cards that Windows lists ahead of it, with no need to change
Windows' *Graphics settings* for FFmpeg.

### When a GPU is missing: Diagnostics

Click **Diagnostics...** at the bottom of the window. It explains, in plain words, why each
graphics card can or can't be used, and saves everything to `diagnostics.txt` (in the
settings folder) to send along with a question:

- the graphics cards Windows lists, with driver versions and any Device Manager problem;
- which FFmpeg is in use, where it came from, and whether it was built with NVIDIA, AMD and Intel support;
- every encoder test, with FFmpeg's own error message (for example *driver too old*, or *amfrt64.dll missing*);
- the last run's log, which shows which encoder each video used and why.

The buttons there:

- **Re-check GPUs** runs the encoder tests again (do this after updating a driver).
- **Test encoders on a video...** burns the first 3 seconds of a video (the first in the list, or one you choose) with every encoder, using your current settings, and reports which work and how fast. Nothing is written next to the video.
- **Re-download FFmpeg** gets the app's own FFmpeg, which includes every GPU encoder and is used ahead of any other FFmpeg on the PC.

Common causes: an NVIDIA driver too old for the FFmpeg build (update it), an AMD driver
installed by Windows Update without AMD's encoder runtime (install Adrenalin from amd.com),
an NVIDIA GeForce MX laptop GPU (it has no video encoder), or a separately installed FFmpeg
without GPU support.

## Where the time comes from

The app uses the first of these it finds:

1. **Sony sidecar XML** (`<clip>M01.XML`): local time plus the camera's own UTC offset.
2. **Apple** `com.apple.quicktime.creationdate`: local time plus offset.
3. **`creation_time`** (UTC): converted using the Windows time zone rules for the recording date. This is the PC's zone unless you choose another one.
4. **The file's modified time**, when the file stores no date at all (e.g. `.mpg`). It's taken as the end of the recording, since that's when the device last saved the file, and copying keeps it. (The file's *created* time isn't used: Windows sets it to when the file was copied.) It's only an estimate, so these files are marked in orange.

A time zone chosen under *Time zone* also applies to files that record their own offset: their
times are converted to it. *Automatic* keeps each camera's own time.

Frame times are read from the file itself, so variable-frame-rate phone video stays accurate.

**Start or end of recording?** *Embedded date is the* decides whether the stored date marks
when recording started or stopped. **Automatic** (the default) goes by the recording format:

| Date found in | Marks the | Why |
|---|---|---|
| Sony sidecar XML | start | Sony writes it when recording begins |
| Apple creation date | start | iPhones and other Apple devices stamp the start |
| `creation_time` from an Android phone (the file has `com.android.*` tags) | end | Android's recorder writes it when recording stops |
| `creation_time` from anything else | start | what cameras usually do |

As soon as videos are added, the file list shows each one's camera, recording date and which
end that date was taken as, so you can check before running. If a device gets it wrong,
choose *Start of recording* or *End of recording* instead. To check a new device, film a clock
that shows seconds and compare it with the timecode.

**Start time** under *Time* overrides the file:

- **From the file:** as above.
- **From the file, shifted by** `+h:mm:ss` or `-h:mm:ss`: corrects a camera clock that was set wrong, e.g. `-1:00:00` if it was an hour fast. It applies to every video in the batch.
- **Set manually:** the date and time of the first frame (to the millisecond), in the chosen time zone. This also works for files that store no date, such as `.mpg`.

**Checking the settings against the videos.** The time settings are checked against the
videos in the list as you change them. Anything that looks wrong turns orange: the video's row
(hover over it for the full explanation), the setting concerned, and a short note under *Time*.
Run asks before going ahead. It flags:

- a video with no date inside, using its modified time instead;
- a date before 2001 or in the future (the camera's clock was probably never set);
- *Start* or *End of recording* chosen when the video's format is known to store the other one;
- a chosen time zone that differs from the offset the camera recorded;
- a manual start time with several videos, which would all start at the same time.

## Command line

```
VideoTimecode.exe [--use-saved | --preset "Name"] [--exit-when-done] [files or folders...]
```

- `--use-saved` starts straight away with the last run's settings.
- `--preset "Name"` starts straight away with a saved preset.
- `--exit-when-done` closes the window when finished (with either option above), which is handy in scripts.
- `--diagnostics [video]` checks the encoders afresh, writes `diagnostics.txt` and opens it, without the main window. With a video, it also test-encodes its first seconds with every encoder.
- The `VIDEO_TIMECODE_HOME` environment variable moves settings, logs and FFmpeg to another folder (e.g. for a portable setup).

## Troubleshooting

- **SmartScreen warns about the exe or `.bat`.** The app isn't code-signed. Choose *More info → Run anyway*, or right-click the zip → *Properties* → *Unblock* before extracting.
- **A video failed.** Click **Open log** at the bottom of the window. It lists the exact ffmpeg command and error for each file.
- **A GPU isn't listed, or isn't used.** Click **Diagnostics...** (see [When a GPU is missing](#when-a-gpu-is-missing-diagnostics)) and send the `diagnostics.txt` it saves.
- **A video is marked "file's modified time".** Its format (e.g. `.mpg`) stores no recording date, so the file's modified time is used as the end of recording. If that's off, set the start time manually.
- **Timecode is an hour off.** Check the *Time zone* setting, and that the camera clock was set correctly.

## Building from source

Requires the .NET 10 SDK.

```
cd src/VideoTimecode
dotnet publish -c Release -o ../../dist
```

The previous PowerShell version is in [`legacy-powershell/`](legacy-powershell).

## License

[MIT](LICENSE). FFmpeg, which is downloaded separately at first run, is GPL-licensed. Its
license is saved next to it in `%LOCALAPPDATA%\VideoTimecode\ffmpeg`.
