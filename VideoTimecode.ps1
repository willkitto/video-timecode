<#
.SYNOPSIS
  Builds a per-frame, time-of-day timecode for video files from their embedded
  recording date, as a subtitle file, an embedded subtitle track, or burned-in text.

.DESCRIPTION
  Recording start time is taken from, in order of preference:
    1. A Sony sidecar XML (<name>M01.XML) - local time with the camera's own UTC offset
    2. The Apple tag com.apple.quicktime.creationdate - local time with offset
    3. The MP4/MOV creation_time tag (UTC), converted to local time with full
       daylight-saving rules for the date of recording (and per frame, so a clip
       that spans a DST change is labelled correctly)

  Every video frame gets its own subtitle cue, so the timecode advances per frame
  in sub-second increments. Frame times are read from the file itself, so
  variable-frame-rate phone video stays accurate.

  Subtitles are written as .ass by default, which carries its own font, color,
  outline and position, so VLC, mpv and the burn-in all look the same. Use
  -SubtitleFormat Srt for players that only understand .srt (styling is then
  mostly up to the player).

  Run without -Mode (e.g. by dropping videos on "Video Timecode - choose settings.bat")
  to get an options window; the choices made there are remembered in
  VideoTimecode.settings.json. -UseSaved ("Video Timecode - use last settings.bat")
  skips the window and reuses those choices. Pass -Mode to skip the window and
  use the command-line options instead.

.PARAMETER Mode
  Subs  - write <name>.ass (or .srt) next to the video; players auto-load it, video untouched
  Embed - also write <name>_timecode.mkv (.mp4 for Srt) with a switchable subtitle track (no re-encode)
  Burn  - also write <name>_timecode_burned.mp4 with the timecode drawn into the picture

.PARAMETER CreationMarks
  Start (default) if the embedded date is when recording began; End if your camera
  stamps the file when recording stops.

.PARAMETER TimeZone
  Windows time zone id (e.g. 'Pacific Standard Time', 'Eastern Standard Time') to use
  for footage shot elsewhere. Defaults to this PC's time zone. Ignored when the file
  carries its own UTC offset (sources 1 and 2 above).

.PARAMETER Format
  .NET date format for the label. Default 'yyyy-MM-dd HH:mm:ss.fff'.

.PARAMETER FontName / FontSize / FontColor / OutlineColor / Position
  Look of the timecode. FontSize is a percentage of the video height. Colors are
  names ('yellow') or hex ('#FFCC00'). Position is BottomRight, BottomLeft,
  BottomCenter, TopRight, TopLeft or TopCenter. A fixed-width font keeps the text
  from shifting as digits change.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Subs', 'Embed', 'Burn')]
    [string]$Mode = 'Subs',
    [ValidateSet('Ass', 'Srt')]
    [string]$SubtitleFormat = 'Ass',
    [ValidateSet('Start', 'End')]
    [string]$CreationMarks = 'Start',
    [string]$TimeZone = '',
    [string]$Format = 'yyyy-MM-dd HH:mm:ss.fff',

    # --- appearance: edit these defaults to change the look of every run ---
    [string]$FontName = 'Consolas',
    [double]$FontSize = 5,
    [bool]$Bold = $true,
    [string]$FontColor = 'White',
    [string]$OutlineColor = 'Black',
    [ValidateSet('BottomRight', 'BottomLeft', 'BottomCenter', 'TopRight', 'TopLeft', 'TopCenter')]
    [string]$Position = 'BottomRight',

    # Skip the window and use the choices saved by the last run of it
    [switch]$UseSaved,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Files
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
Add-Type -AssemblyName System.Drawing

function Find-Tool($name) {
    $local = Join-Path $PSScriptRoot "$name.exe"
    if (Test-Path $local) { return $local }
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "$name not found. Install it with:  winget install Gyan.FFmpeg   (or put $name.exe next to this script)"
}

function ConvertTo-Rgb([string]$c) {
    $h = $c.Trim().TrimStart('#')
    if ($h -match '^[0-9a-fA-F]{6}$') {
        return @([Convert]::ToInt32($h.Substring(0, 2), 16), [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16))
    }
    $named = [System.Drawing.Color]::FromName($c.Trim())
    if (-not $named.IsKnownColor) { throw "Unknown color '$c'. Use a name like 'yellow' or hex like '#FFCC00'." }
    return @($named.R, $named.G, $named.B)
}

# ASS colors are &HAABBGGRR
function ConvertTo-AssColor([string]$c) { $r, $g, $b = ConvertTo-Rgb $c; '&H00{0:X2}{1:X2}{2:X2}' -f $b, $g, $r }
function ConvertTo-HtmlColor([string]$c) { $r, $g, $b = ConvertTo-Rgb $c; '#{0:X2}{1:X2}{2:X2}' -f $r, $g, $b }

function Get-RecordingStart([string]$path, [string]$ffprobe) {
    # 1. Sony sidecar XML
    $dir = Split-Path $path -Parent
    $base = [IO.Path]::GetFileNameWithoutExtension($path)
    $xml = Join-Path $dir "${base}M01.XML"
    if (Test-Path $xml) {
        $doc = [xml](Get-Content -LiteralPath $xml -Raw)
        $v = $doc.NonRealTimeMeta.CreationDate.value
        if ($v) {
            return @{ Time = [DateTimeOffset]::Parse($v, $inv); FixedOffset = $true; Source = "Sony sidecar $(Split-Path $xml -Leaf)" }
        }
    }

    $tags = & $ffprobe -v error -show_entries format_tags -of json $path | ConvertFrom-Json
    $t = $tags.format.tags

    # 2. Apple local-time tag (has offset)
    $apple = $t.'com.apple.quicktime.creationdate'
    if ($apple) {
        return @{ Time = [DateTimeOffset]::Parse($apple, $inv); FixedOffset = $true; Source = 'com.apple.quicktime.creationdate' }
    }

    # 3. Standard creation_time (UTC)
    $ct = $t.creation_time
    if ($ct) {
        $utc = [DateTimeOffset]::Parse($ct, $inv, [Globalization.DateTimeStyles]::AssumeUniversal)
        return @{ Time = $utc.ToUniversalTime(); FixedOffset = $false; Source = 'creation_time (UTC)' }
    }
    throw 'No creation date found in the file.'
}

function Get-FrameTimes([string]$path, [string]$ffprobe) {
    # Packet timestamps need no decoding, so this is fast even for long clips.
    $lines = & $ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 $path
    $times = [Collections.Generic.List[double]]::new()
    foreach ($l in $lines) {
        $s = "$l".Trim().TrimEnd(',')
        if ($s -and $s -ne 'N/A') { $times.Add([double]::Parse($s, $inv)) }
    }
    if ($times.Count -eq 0) { throw 'No video frames found.' }
    $times.Sort()
    return $times
}

function Get-DisplaySize([string]$path, [string]$ffprobe) {
    $j = & $ffprobe -v error -select_streams v:0 -show_entries stream=width,height:stream_side_data=rotation -of json $path | ConvertFrom-Json
    $s = $j.streams[0]
    $rot = ($s.side_data_list | Where-Object { $null -ne $_.rotation } | Select-Object -First 1).rotation
    # Portrait phone video is stored sideways with a rotation flag
    if ($rot -and [math]::Abs([int]$rot) % 180 -eq 90) { return @([int]$s.height, [int]$s.width) }
    return @([int]$s.width, [int]$s.height)
}

function Format-SrtTime([long]$ms) {
    $ts = [TimeSpan]::FromMilliseconds($ms)
    '{0:00}:{1:00}:{2:00},{3:000}' -f [math]::Floor($ts.TotalHours), $ts.Minutes, $ts.Seconds, $ts.Milliseconds
}

function Format-AssTime([long]$cs) {
    $ts = [TimeSpan]::FromMilliseconds($cs * 10)
    '{0}:{1:00}:{2:00}.{3:00}' -f [math]::Floor($ts.TotalHours), $ts.Minutes, $ts.Seconds, [int]($ts.Milliseconds / 10)
}

function Get-AssHeader([int]$w, [int]$h) {
    $align = @{ BottomLeft = 1; BottomCenter = 2; BottomRight = 3; TopLeft = 7; TopCenter = 8; TopRight = 9 }[$Position]
    $size = [math]::Round($h * $FontSize / 100)
    $outline = [math]::Max(1, [math]::Round($size / 14))
    $margin = [math]::Round($h / 30)
    $fc = ConvertTo-AssColor $FontColor
    $oc = ConvertTo-AssColor $OutlineColor
    @"
[Script Info]
ScriptType: v4.00+
PlayResX: $w
PlayResY: $h
ScaledBorderAndShadow: yes
WrapStyle: 2

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Timecode,$FontName,$size,$fc,$fc,$oc,&H80000000,$(if ($Bold) { -1 } else { 0 }),0,0,0,100,100,0,0,1,$outline,0,$align,$margin,$margin,$margin,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text

"@ -replace "`r?`n", "`r`n"
}

function Write-TimecodeSubs([string]$path, [string]$subPath, [string]$ffprobe, $tz) {
    $start = Get-RecordingStart $path $ffprobe
    $times = Get-FrameTimes $path $ffprobe

    $first = $times[0]
    $n = $times.Count
    $frameDur = if ($n -gt 1) { ($times[$n - 1] - $first) / ($n - 1) } else { 1 / 30 }
    $clipLen = $times[$n - 1] + $frameDur - $first

    $t0 = $start.Time
    if ($CreationMarks -eq 'End') { $t0 = $t0.AddTicks(-[long]($clipLen * 1e7)) }

    $isAss = $SubtitleFormat -eq 'Ass'
    $sb = [Text.StringBuilder]::new()
    if ($isAss) {
        $w, $h = Get-DisplaySize $path $ffprobe
        [void]$sb.Append((Get-AssHeader $w $h))
    }
    else {
        $srtOpen = '<font face="{0}" color="{1}">' -f $FontName, (ConvertTo-HtmlColor $FontColor)
        $srtClose = '</font>'
        if ($Bold) { $srtOpen += '<b>'; $srtClose = '</b></font>' }
    }

    for ($i = 0; $i -lt $n; $i++) {
        $rel = $times[$i] - $first
        $next = if ($i + 1 -lt $n) { $times[$i + 1] - $first } else { $rel + $frameDur }
        $at = $t0.AddTicks([long][math]::Round($rel * 1e7))
        # Per-frame conversion so a clip crossing a DST change is labelled correctly
        if (-not $start.FixedOffset) { $at = [TimeZoneInfo]::ConvertTime($at, $tz) }
        $label = $at.ToString($Format, $inv)
        if ($isAss) {
            # ASS times are in centiseconds; round down so each cue begins at or before its frame
            $a = Format-AssTime ([long][math]::Floor($rel * 100))
            $b = Format-AssTime ([long][math]::Floor($next * 100))
            [void]$sb.Append("Dialogue: 0,$a,$b,Timecode,,0,0,0,,$label`r`n")
        }
        else {
            $a = Format-SrtTime ([long][math]::Floor($rel * 1000))
            $b = Format-SrtTime ([long][math]::Floor($next * 1000))
            [void]$sb.Append("$($i + 1)`r`n$a --> $b`r`n$srtOpen$label$srtClose`r`n`r`n")
        }
    }
    [IO.File]::WriteAllText($subPath, $sb.ToString(), [Text.UTF8Encoding]::new($false))

    $label0 = if ($start.FixedOffset) { $t0 } else { [TimeZoneInfo]::ConvertTime($t0, $tz) }
    Write-Host "  Date source : $($start.Source)"
    Write-Host ("  First frame : {0}  (UTC{1})" -f $label0.ToString($Format, $inv), $label0.ToString('zzz', $inv))
    Write-Host ("  Frames      : {0}  ({1:0.###} s)" -f $n, $clipLen)
}

# --- remembered settings ----------------------------------------------------

$settingsPath = Join-Path $PSScriptRoot 'VideoTimecode.settings.json'
$settingKeys = 'Mode', 'FontName', 'FontSize', 'Bold', 'FontColor', 'OutlineColor', 'Position', 'Format', 'CreationMarks', 'TimeZone'

function Import-Settings {
    if (-not (Test-Path -LiteralPath $settingsPath)) { return }
    try { $s = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch { return }
    foreach ($p in $s.PSObject.Properties) {
        # A bad value (e.g. hand-edited) just keeps the default
        if ($settingKeys -contains $p.Name) { try { Set-Variable -Scope Script -Name $p.Name -Value $p.Value } catch {} }
    }
}

function Save-Settings {
    $o = [ordered]@{}
    foreach ($k in $settingKeys) { $o[$k] = (Get-Variable -Scope Script -Name $k).Value }
    try { $o | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8 } catch {}
}

# --- options window ---------------------------------------------------------

function ConvertTo-DrawingColor([string]$c) { $r, $g, $b = ConvertTo-Rgb $c; [Drawing.Color]::FromArgb($r, $g, $b) }

function Test-EvenDigits([string]$family) {
    # Fonts whose digits all share one width don't shift as the time ticks over
    try { $f = New-Object Drawing.Font($family, 20, [Drawing.GraphicsUnit]::Pixel) } catch { return $false }
    try {
        $w = foreach ($d in 0..9) { [Windows.Forms.TextRenderer]::MeasureText("$d" * 10, $f).Width }
        $sorted = $w | Sort-Object
        return ($sorted[-1] - $sorted[0]) -le 1
    }
    finally { $f.Dispose() }
}

function Show-OptionsDialog([string[]]$initialFiles) {
    Add-Type -AssemblyName System.Windows.Forms
    try {
        Add-Type -Namespace VTC -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
        [void][VTC.Native]::SetProcessDPIAware()
    }
    catch {}
    [Windows.Forms.Application]::EnableVisualStyles()
    $gfx = [Drawing.Graphics]::FromHwnd([IntPtr]::Zero); $S = $gfx.DpiX / 96; $gfx.Dispose()
    function px([double]$n) { [int][math]::Round($n * $S) }
    function pad($l, $t, $r, $b) { New-Object Windows.Forms.Padding -ArgumentList (px $l), (px $t), (px $r), (px $b) }
    function New-Label([string]$text, [switch]$Header) {
        $l = New-Object Windows.Forms.Label
        $l.Text = $text; $l.AutoSize = $true; $l.Anchor = 'Left'
        if ($Header) { $l.Font = New-Object Drawing.Font('Segoe UI', 10, [Drawing.FontStyle]::Bold); $l.Margin = pad 0 12 0 4 }
        $l
    }
    function New-Table([int]$cols) {
        $t = New-Object Windows.Forms.TableLayoutPanel
        $t.ColumnCount = $cols; $t.AutoSize = $true; $t.AutoSizeMode = 'GrowAndShrink'; $t.Margin = pad 12 0 0 0
        $t
    }

    Write-Host 'Checking installed fonts...'
    $allFonts = (New-Object Drawing.Text.InstalledFontCollection).Families | ForEach-Object Name
    $evenFonts = @($allFonts | Where-Object { Test-EvenDigits $_ })

    $positions = @(
        [pscustomobject]@{ Name = 'Bottom right'; Value = 'BottomRight' }
        [pscustomobject]@{ Name = 'Bottom left'; Value = 'BottomLeft' }
        [pscustomobject]@{ Name = 'Bottom center'; Value = 'BottomCenter' }
        [pscustomobject]@{ Name = 'Top right'; Value = 'TopRight' }
        [pscustomobject]@{ Name = 'Top left'; Value = 'TopLeft' }
        [pscustomobject]@{ Name = 'Top center'; Value = 'TopCenter' }
    )
    $ui = @{ Fore = (ConvertTo-HtmlColor $FontColor); Outline = (ConvertTo-HtmlColor $OutlineColor) }

    $form = New-Object Windows.Forms.Form
    $form.Text = 'Video Timecode'
    $form.Font = New-Object Drawing.Font('Segoe UI', 9)
    $form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false; $form.MinimizeBox = $false
    $form.StartPosition = 'CenterScreen'; $form.TopMost = $true

    $root = New-Table 1
    $root.Location = New-Object Drawing.Point (px 12), (px 4)
    $root.Margin = pad 0 0 0 0

    # 1. Videos
    $root.Controls.Add((New-Label '1. Videos  (drag more files into the list)' -Header))
    $tFiles = New-Table 2
    $lst = New-Object Windows.Forms.ListBox
    $lst.Width = px 600; $lst.Height = px 100; $lst.HorizontalScrollbar = $true
    $lst.SelectionMode = 'MultiExtended'; $lst.AllowDrop = $true
    $addFiles = { param([string[]]$paths) foreach ($p in $paths) { if (-not $lst.Items.Contains($p)) { [void]$lst.Items.Add($p) } } }
    & $addFiles $initialFiles
    $lst.Add_DragEnter({ if ($_.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop)) { $_.Effect = 'Copy' } })
    $lst.Add_DragDrop({ & $addFiles $_.Data.GetData([Windows.Forms.DataFormats]::FileDrop) })
    $fileBtns = New-Object Windows.Forms.FlowLayoutPanel
    $fileBtns.FlowDirection = 'TopDown'; $fileBtns.AutoSize = $true
    $btnAdd = New-Object Windows.Forms.Button -Property @{ Text = 'Add...'; Width = (px 90); Height = (px 28) }
    $btnRemove = New-Object Windows.Forms.Button -Property @{ Text = 'Remove'; Width = (px 90); Height = (px 28) }
    $btnAdd.Add_Click({
            $d = New-Object Windows.Forms.OpenFileDialog
            $d.Multiselect = $true
            $d.Filter = 'Video files|*.mp4;*.mov;*.m4v;*.mts;*.m2ts;*.avi;*.mkv;*.mxf|All files|*.*'
            if ($d.ShowDialog($form) -eq 'OK') { & $addFiles $d.FileNames }
        })
    $btnRemove.Add_Click({ foreach ($i in @($lst.SelectedItems)) { $lst.Items.Remove($i) } })
    $fileBtns.Controls.AddRange(@($btnAdd, $btnRemove))
    $tFiles.Controls.AddRange(@($lst, $fileBtns))
    $root.Controls.Add($tFiles)

    # 2. Output
    $root.Controls.Add((New-Label '2. Output' -Header))
    $outFlow = New-Object Windows.Forms.FlowLayoutPanel
    $outFlow.FlowDirection = 'TopDown'; $outFlow.AutoSize = $true; $outFlow.Margin = pad 12 0 0 0
    $modeTexts = @(
        'External subtitle file  (.ass next to the video)  -  fastest; the video is not touched'
        'Subtitle stream in a new .mkv  -  turn it on/off in the player; no quality loss'
        'Burn into the picture  (new .mp4)  -  always visible everywhere; re-encodes the video'
    )
    $modeValues = 'Subs', 'Embed', 'Burn'
    $radios = foreach ($i in 0..2) {
        $rb = New-Object Windows.Forms.RadioButton -Property @{ Text = $modeTexts[$i]; AutoSize = $true }
        $rb.Checked = ($modeValues[$i] -eq $Mode)
        $outFlow.Controls.Add($rb); $rb
    }
    if (-not ($radios | Where-Object Checked)) { $radios[0].Checked = $true }
    $root.Controls.Add($outFlow)

    # 3. Appearance
    $root.Controls.Add((New-Label '3. Appearance' -Header))
    $tLook = New-Table 3
    $cmbFont = New-Object Windows.Forms.ComboBox -Property @{ DropDownStyle = 'DropDownList'; Width = (px 240); MaxDropDownItems = 20 }
    $chkEven = New-Object Windows.Forms.CheckBox -Property @{ Text = 'Only fonts with even-width digits (no wobble)'; AutoSize = $true; Checked = $true }
    $fillFonts = {
        $keep = if ($cmbFont.SelectedItem) { [string]$cmbFont.SelectedItem } else { $FontName }
        $cmbFont.Items.Clear()
        $list = if ($chkEven.Checked) { $evenFonts } else { $allFonts }
        foreach ($n in $list) { [void]$cmbFont.Items.Add($n) }
        $idx = $cmbFont.Items.IndexOf($keep)
        if ($idx -lt 0) { $idx = $cmbFont.Items.IndexOf('Consolas') }
        $cmbFont.SelectedIndex = [math]::Max(0, $idx)
    }
    if ($evenFonts -notcontains $FontName -and $allFonts -contains $FontName) { $chkEven.Checked = $false }
    & $fillFonts
    $chkEven.Add_CheckedChanged($fillFonts)

    $sizeFlow = New-Object Windows.Forms.FlowLayoutPanel -Property @{ AutoSize = $true; WrapContents = $false; Margin = (pad 0 0 0 0) }
    $numSize = New-Object Windows.Forms.NumericUpDown -Property @{ Minimum = 1; Maximum = 30; DecimalPlaces = 1; Increment = 0.5; Width = (px 60) }
    $numSize.Value = [decimal][math]::Min(30, [math]::Max(1, $FontSize))
    $lblPct = New-Label '% of video height'
    $chkBold = New-Object Windows.Forms.CheckBox -Property @{ Text = 'Bold'; AutoSize = $true; Checked = $Bold; Margin = (pad 16 3 0 0) }
    $sizeFlow.Controls.AddRange(@($numSize, $lblPct, $chkBold))

    function Set-ColorButton($btn, [string]$hex) {
        $c = ConvertTo-DrawingColor $hex
        $btn.UseVisualStyleBackColor = $false; $btn.BackColor = $c; $btn.Text = $hex
        $btn.ForeColor = if (($c.R * 299 + $c.G * 587 + $c.B * 114) / 1000 -gt 128) { 'Black' } else { 'White' }
    }
    $btnFore = New-Object Windows.Forms.Button -Property @{ Width = (px 110); Height = (px 28) }
    $btnOutline = New-Object Windows.Forms.Button -Property @{ Width = (px 110); Height = (px 28) }
    Set-ColorButton $btnFore $ui.Fore; Set-ColorButton $btnOutline $ui.Outline

    $cmbPos = New-Object Windows.Forms.ComboBox -Property @{ DropDownStyle = 'DropDownList'; Width = (px 160) }
    foreach ($p in $positions) { [void]$cmbPos.Items.Add($p.Name) }
    $cmbPos.SelectedIndex = [math]::Max(0, [array]::IndexOf(@($positions.Value), $Position))

    $pic = New-Object Windows.Forms.PictureBox -Property @{ Width = (px 384); Height = (px 216); BorderStyle = 'FixedSingle'; Margin = (pad 20 3 0 0) }

    $rows = @(
        @('Font', $cmbFont), @('', $chkEven), @('Size', $sizeFlow),
        @('Text color', $btnFore), @('Outline color', $btnOutline), @('Position', $cmbPos)
    )
    for ($r = 0; $r -lt $rows.Count; $r++) {
        $tLook.Controls.Add((New-Label $rows[$r][0]), 0, $r)
        $rows[$r][1].Anchor = 'Left'
        $tLook.Controls.Add($rows[$r][1], 1, $r)
    }
    # An empty last row soaks up the preview's extra height so the rows above stay compact
    $tLook.Controls.Add((New-Label ''), 0, $rows.Count)
    $tLook.Controls.Add($pic, 2, 0)
    $tLook.SetRowSpan($pic, $rows.Count + 1)
    $root.Controls.Add($tLook)

    # 4. Time
    $root.Controls.Add((New-Label '4. Time' -Header))
    $tTime = New-Table 2
    $cmbFormat = New-Object Windows.Forms.ComboBox -Property @{ DropDownStyle = 'DropDown'; Width = (px 260) }
    $formats = 'yyyy-MM-dd HH:mm:ss.fff', 'yyyy-MM-dd HH:mm:ss.ff', 'yyyy-MM-dd HH:mm:ss', 'HH:mm:ss.fff',
    'yyyy-MM-dd hh:mm:ss.fff tt', 'MM/dd/yyyy hh:mm:ss.fff tt', 'dd.MM.yyyy HH:mm:ss.fff'
    foreach ($fm in $formats) { [void]$cmbFormat.Items.Add($fm) }
    $cmbFormat.Text = $Format
    $lblFmtHelp = New-Label 'HH=24h, hh=12h, tt=AM/PM, fff=milliseconds'
    $fmtFlow = New-Object Windows.Forms.FlowLayoutPanel -Property @{ AutoSize = $true; WrapContents = $false; Margin = (pad 0 0 0 0) }
    $fmtFlow.Controls.AddRange(@($cmbFormat, $lblFmtHelp))

    $cmbMarks = New-Object Windows.Forms.ComboBox -Property @{ DropDownStyle = 'DropDownList'; Width = (px 420) }
    [void]$cmbMarks.Items.Add('Start of recording  (verified on Sony FDR-AX43)')
    [void]$cmbMarks.Items.Add('End of recording')
    $cmbMarks.SelectedIndex = if ($CreationMarks -eq 'End') { 1 } else { 0 }

    $cmbTz = New-Object Windows.Forms.ComboBox -Property @{ DropDownStyle = 'DropDownList'; Width = (px 420); MaxDropDownItems = 20 }
    [void]$cmbTz.Items.Add("Automatic  (camera's own offset if recorded, else this PC's zone)")
    $zones = [TimeZoneInfo]::GetSystemTimeZones()
    foreach ($z in $zones) { [void]$cmbTz.Items.Add($z.DisplayName) }
    $cmbTz.SelectedIndex = 0
    for ($i = 0; $i -lt $zones.Count; $i++) { if ($zones[$i].Id -eq $TimeZone) { $cmbTz.SelectedIndex = $i + 1 } }

    $fmtFlow.Anchor = 'Left'; $cmbMarks.Anchor = 'Left'; $cmbTz.Anchor = 'Left'
    $tTime.Controls.Add((New-Label 'Label format'), 0, 0); $tTime.Controls.Add($fmtFlow, 1, 0)
    $tTime.Controls.Add((New-Label 'Embedded date is the'), 0, 1); $tTime.Controls.Add($cmbMarks, 1, 1)
    $tTime.Controls.Add((New-Label 'Time zone'), 0, 2); $tTime.Controls.Add($cmbTz, 1, 2)
    $root.Controls.Add($tTime)

    # Buttons
    $btnFlow = New-Object Windows.Forms.FlowLayoutPanel -Property @{ FlowDirection = 'RightToLeft'; AutoSize = $true; Anchor = 'Right'; Margin = (pad 0 16 0 8) }
    $btnRun = New-Object Windows.Forms.Button -Property @{ Text = 'Run'; Width = (px 100); Height = (px 32) }
    $btnCancel = New-Object Windows.Forms.Button -Property @{ Text = 'Cancel'; Width = (px 100); Height = (px 32); DialogResult = 'Cancel' }
    $btnFlow.Controls.AddRange(@($btnRun, $btnCancel))
    $root.Controls.Add($btnFlow)
    $form.AcceptButton = $btnRun; $form.CancelButton = $btnCancel

    # Live preview of a 16:9 frame
    $pic.Add_Paint({
            param($sender, $e)
            $g = $e.Graphics; $g.SmoothingMode = 'AntiAlias'
            $r = $sender.ClientRectangle
            $bg = New-Object Drawing.Drawing2D.LinearGradientBrush($r, [Drawing.Color]::FromArgb(35, 40, 50), [Drawing.Color]::FromArgb(215, 205, 180), [single]0)
            $g.FillRectangle($bg, $r); $bg.Dispose()
            $fam = try { New-Object Drawing.FontFamily([string]$cmbFont.SelectedItem) } catch { [Drawing.FontFamily]::GenericMonospace }
            $style = [Drawing.FontStyle]::Regular
            if ($chkBold.Checked -and $fam.IsStyleAvailable([Drawing.FontStyle]::Bold)) { $style = [Drawing.FontStyle]::Bold }
            elseif (-not $fam.IsStyleAvailable($style)) { $style = [Drawing.FontStyle]::Bold }
            $h = $r.Height
            $size = $h * [double]$numSize.Value / 100
            # ASS font size is the full line height, GDI+ wants the em size
            $em = $size * $fam.GetEmHeight($style) / ($fam.GetCellAscent($style) + $fam.GetCellDescent($style))
            $text = try { (Get-Date).ToString($cmbFormat.Text, $inv) } catch { 'invalid format' }
            $path = New-Object Drawing.Drawing2D.GraphicsPath
            $path.AddString($text, $fam, [int]$style, [single]$em, (New-Object Drawing.PointF 0, 0), [Drawing.StringFormat]::GenericTypographic)
            $b = $path.GetBounds()
            $m = $h / 30
            $pos = $positions[[math]::Max(0, $cmbPos.SelectedIndex)].Value
            $x = if ($pos -like '*Left') { $m } elseif ($pos -like '*Right') { $r.Width - $m - $b.Width } else { ($r.Width - $b.Width) / 2 }
            $y = if ($pos -like 'Top*') { $m } else { $h - $m - $b.Height }
            $mx = New-Object Drawing.Drawing2D.Matrix
            $mx.Translate([single]($x - $b.X), [single]($y - $b.Y)); $path.Transform($mx)
            $pen = New-Object Drawing.Pen((ConvertTo-DrawingColor $ui.Outline), [single](2 * [math]::Max(1, $size / 14)))
            $pen.LineJoin = 'Round'
            $g.DrawPath($pen, $path)
            $brush = New-Object Drawing.SolidBrush (ConvertTo-DrawingColor $ui.Fore)
            $g.FillPath($brush, $path)
            $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $mx.Dispose(); $fam.Dispose()
        })
    $redraw = { $pic.Invalidate() }
    $cmbFont.Add_SelectedIndexChanged($redraw); $numSize.Add_ValueChanged($redraw); $chkBold.Add_CheckedChanged($redraw)
    $cmbPos.Add_SelectedIndexChanged($redraw); $cmbFormat.Add_TextChanged($redraw)
    $pickColor = {
        param($btn, $key)
        $d = New-Object Windows.Forms.ColorDialog
        $d.FullOpen = $true; $d.Color = ConvertTo-DrawingColor $ui[$key]
        if ($d.ShowDialog($form) -eq 'OK') {
            $ui[$key] = '#{0:X2}{1:X2}{2:X2}' -f $d.Color.R, $d.Color.G, $d.Color.B
            Set-ColorButton $btn $ui[$key]; $pic.Invalidate()
        }
    }
    $btnFore.Add_Click({ & $pickColor $btnFore 'Fore' })
    $btnOutline.Add_Click({ & $pickColor $btnOutline 'Outline' })

    $btnRun.Add_Click({
            if ($lst.Items.Count -eq 0) {
                [void][Windows.Forms.MessageBox]::Show($form, 'Add at least one video first.', 'Video Timecode')
                return
            }
            $form.DialogResult = 'OK'
        })

    $form.Controls.Add($root)
    $form.Add_Load({
            $root.PerformLayout()
            $form.ClientSize = New-Object Drawing.Size ($root.Right + (px 12)), ($root.Bottom + (px 4))
        })

    if ($form.ShowDialog() -ne 'OK') { return $null }

    $script:Mode = $modeValues[[array]::IndexOf(@($radios | ForEach-Object Checked), $true)]
    $script:FontName = [string]$cmbFont.SelectedItem
    $script:FontSize = [double]$numSize.Value
    $script:Bold = $chkBold.Checked
    $script:FontColor = $ui.Fore
    $script:OutlineColor = $ui.Outline
    $script:Position = $positions[$cmbPos.SelectedIndex].Value
    $script:Format = $cmbFormat.Text
    $script:CreationMarks = if ($cmbMarks.SelectedIndex -eq 1) { 'End' } else { 'Start' }
    $script:TimeZone = if ($cmbTz.SelectedIndex -le 0) { '' } else { $zones[$cmbTz.SelectedIndex - 1].Id }
    return , [string[]]@($lst.Items)
}

# --- main -----------------------------------------------------------------

if ($UseSaved -and (Test-Path -LiteralPath $settingsPath) -and $Files) {
    Import-Settings
    Write-Host "Using saved settings: $Mode, $FontName $FontSize%, $FontColor on $OutlineColor, $Position"
}
elseif (-not $PSBoundParameters.ContainsKey('Mode')) {
    # Also reached with -UseSaved before anything has been saved, so the first run sets the defaults
    if ($UseSaved) { Write-Host 'No saved settings yet - choose them once in the window.' }
    Import-Settings
    $initial = @(foreach ($f in $Files) { if (Test-Path -LiteralPath $f) { (Resolve-Path -LiteralPath $f).ProviderPath } })
    $Files = Show-OptionsDialog $initial
    if (-not $Files) { Write-Host 'Cancelled.'; exit 0 }
    Save-Settings
}
elseif (-not $Files -or $Files.Count -eq 0) {
    Write-Host 'Drop videos on "Video Timecode.bat" for the options window, or run from the command line:'
    Write-Host 'Usage: VideoTimecode.ps1 -Mode Subs|Embed|Burn [-FontColor yellow] [-Position TopLeft] [-CreationMarks Start|End] [-TimeZone "<Windows TZ id>"] <files...>'
    Write-Host 'Run  Get-Help .\VideoTimecode.ps1 -Detailed  for every option.'
    exit 1
}

# Fail early on a bad color rather than once per file
$null = ConvertTo-Rgb $FontColor; $null = ConvertTo-Rgb $OutlineColor

$ffprobe = Find-Tool 'ffprobe'
$ffmpeg = if ($Mode -ne 'Subs') { Find-Tool 'ffmpeg' } else { $null }
$tz = if ($TimeZone) { [TimeZoneInfo]::FindSystemTimeZoneById($TimeZone) } else { [TimeZoneInfo]::Local }
$subExt = '.' + $SubtitleFormat.ToLowerInvariant()

$failed = 0
foreach ($f in $Files) {
    $path = (Resolve-Path -LiteralPath $f).ProviderPath
    Write-Host "`n$path"
    try {
        $dir = Split-Path $path -Parent
        $base = [IO.Path]::GetFileNameWithoutExtension($path)
        $sub = Join-Path $dir "$base$subExt"
        Write-TimecodeSubs $path $sub $ffprobe $tz
        Write-Host "  Subtitles   : $sub"

        if ($Mode -eq 'Embed') {
            # MP4's only text format (mov_text) drops styling, so styled ASS goes in MKV.
            if ($SubtitleFormat -eq 'Ass') { $out = Join-Path $dir "${base}_timecode.mkv"; $subCodec = 'ass' }
            else { $out = Join-Path $dir "${base}_timecode.mp4"; $subCodec = 'mov_text' }
            & $ffmpeg -v error -y -i $path -i $sub -map 0:v -map 0:a? -map 1:0 -c copy -c:s $subCodec `
                -metadata:s:s:0 language=eng -metadata:s:s:0 title=Timecode -disposition:s:0 default `
                -map_metadata 0 $out
            if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed while embedding subtitles.' }
            Write-Host "  Video       : $out  (turn subtitles on in your player)"
        }
        elseif ($Mode -eq 'Burn') {
            $out = Join-Path $dir "${base}_timecode_burned.mp4"
            # The subtitles filter is picky about Windows paths, so run it from a temp dir
            # with a plain file name.
            $tmp = Join-Path ([IO.Path]::GetTempPath()) ("vtc_" + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory $tmp | Out-Null
            Copy-Item -LiteralPath $sub (Join-Path $tmp "tc$subExt")
            Push-Location -LiteralPath $tmp
            try {
                & $ffmpeg -v error -stats -y -i $path -map 0:v:0 -map 0:a? `
                    -vf "subtitles=tc$subExt" `
                    -c:v libx264 -crf 18 -preset medium -pix_fmt yuv420p -c:a aac -b:a 256k `
                    -map_metadata 0 -movflags +faststart $out
                if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed while burning in the timecode.' }
            }
            finally {
                Pop-Location
                Remove-Item -Recurse -Force $tmp
            }
            Write-Host "  Video       : $out"
        }
    }
    catch {
        $failed++
        Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ''
if ($failed) { Write-Host "$failed file(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'Done.' -ForegroundColor Green
