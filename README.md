# Video Timecode

Adds a running, frame-accurate **time-of-day timecode** to your videos, using the
recording date the camera embedded in the file. Drag videos onto a `.bat` file,
pick a style, done.

![Options window](docs/screenshot.png)

- **Per-frame, sub-second labels** such as `2026-10-25 15:48:14.033`, with each frame getting its own label
- **Three output types:**
  - an external subtitle file next to the video, which leaves the video untouched
  - a subtitle stream in a new `.mkv` that you can switch on and off in the player, with no quality loss
  - burned into the picture as a new `.mp4`
- **Your choice of font, size, colors and position**, with a live preview. Fixed-width digits keep the text from wobbling as the time ticks.
- **Daylight saving time handled** for the date of recording, and frame by frame for clips that span a clock change
- **Remembers your last settings.** A second launcher reuses them with no window.
- Your original files are never modified

## Download

1. Download `video-timecode-vX.Y.Z.zip` from the [latest release](../../releases/latest) and extract it anywhere.
2. Install FFmpeg (one time) in a terminal:
   ```
   winget install Gyan.FFmpeg
   ```
   Or put `ffmpeg.exe` and `ffprobe.exe` next to `VideoTimecode.ps1`.

Requires Windows 10 or 11. PowerShell 5.1, which comes with Windows, is enough.

## Usage

| Drop videos on | What happens |
|---|---|
| `Video Timecode - choose settings.bat` | Opens the options window, runs, and saves your choices as the new defaults |
| `Video Timecode - use last settings.bat` | Runs immediately with the saved choices. The first time, it opens the window once. |

You can drop several videos at once. Outputs are written next to each video:

| Output type | Files created |
|---|---|
| External subtitle file | `clip.ass`. VLC and mpv load it automatically when you open `clip.mp4`. |
| Subtitle stream | `clip.ass` + `clip_timecode.mkv` (video and audio copied, not re-encoded) |
| Burn into picture | `clip.ass` + `clip_timecode_burned.mp4` (re-encoded with x264, CRF 18) |

## Where the time comes from

The script uses the first of these sources it finds:

1. **Sony sidecar XML** (`<clip>M01.XML`): local time plus the camera's own UTC offset.
2. **Apple** `com.apple.quicktime.creationdate`: local time plus offset.
3. **`creation_time`** in the MP4/MOV (UTC): converted with the Windows time zone rules for the recording date. This is the PC's own zone unless you pick another one.

Frame times are read from the file's own timestamps, so variable-frame-rate phone video stays accurate.

### Start or end of recording?

Most files store the time recording **started**, but not every device does. On the
Sony FDR-AX43, the embedded date is the start: the camera writes the thumbnail when
recording begins and the XML when it stops, and the embedded time matches the
thumbnail. If your device stamps the **end**, choose "End of recording" in the
window. To check a new device, film a clock that shows seconds and compare it with
the timecode.

The timecode is only as accurate as the camera's clock.

## Command line

The window is optional. Pass `-Mode` to run without it:

```
powershell -ExecutionPolicy Bypass -File VideoTimecode.ps1 -Mode Burn -FontColor yellow -Position TopLeft -FontSize 7 clip1.mp4 clip2.mp4
```

| Option | Values |
|---|---|
| `-Mode` | `Subs`, `Embed`, `Burn` |
| `-FontName`, `-FontSize` (% of height), `-Bold $true/$false` | |
| `-FontColor`, `-OutlineColor` | color name (`yellow`) or hex (`#FFCC00`) |
| `-Position` | `BottomRight`, `BottomLeft`, `BottomCenter`, `TopRight`, `TopLeft`, `TopCenter` |
| `-Format` | .NET date format, default `yyyy-MM-dd HH:mm:ss.fff` |
| `-CreationMarks` | `Start` (default) or `End` |
| `-TimeZone` | Windows time zone id, e.g. `"Eastern Standard Time"` |
| `-SubtitleFormat` | `Ass` (default, styled) or `Srt` (basic; styling mostly up to the player) |
| `-UseSaved` | use the settings saved by the window |

Run `Get-Help .\VideoTimecode.ps1 -Detailed` for the full help.

## Troubleshooting

- **Windows SmartScreen warns about the `.bat` file.** This happens because the file was downloaded. Choose *More info → Run anyway*, or right-click the zip → *Properties* → *Unblock* before extracting.
- **The subtitles don't show in the `.mkv`.** Turn subtitles on in the player (VLC: *Subtitle → Sub Track*).
- **Timecode is an hour off.** Check the *Time zone* setting, and that the camera's clock was set correctly.

## License

[MIT](LICENSE)
