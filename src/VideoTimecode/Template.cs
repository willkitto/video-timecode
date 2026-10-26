using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoTimecode;

// Per-file values the overlay tokens draw on
sealed class TemplateContext
{
    public VideoInfo Info { get; }
    public AppSettings Settings { get; }
    public TimeZoneInfo Zone { get; }
    public DateTimeOffset T0 { get; }        // time of the first frame
    public int Frames { get; }
    public double ClipLength { get; }
    public long TcStart { get; }
    public int TcFps { get; }
    public bool TcDrop { get; }

    public TemplateContext(VideoInfo info, AppSettings settings, TimeZoneInfo zone)
    {
        Info = info;
        Settings = settings;
        Zone = zone;

        var t = info.FrameTimes;
        Frames = t.Count > 0 ? t.Count : (int)Math.Round(info.Duration * info.Fps);
        double frameDur = t.Count > 1 ? (t[^1] - t[0]) / (t.Count - 1) : 1 / Math.Max(1, info.Fps);
        ClipLength = t.Count > 0 ? t[^1] + frameDur - t[0] : info.Duration;

        T0 = settings.CreationMarks == CreationMarks.End
            ? info.Start.AddTicks(-(long)Math.Round(ClipLength * 1e7))
            : info.Start;

        Smpte.TryParse(info.Timecode, info.Fps, out var start, out var nominal, out var drop);
        TcStart = start;
        TcFps = nominal;
        TcDrop = drop;
    }

    // Wall-clock time of a moment in the clip; converted per moment so a clip that
    // crosses a daylight-saving change is labelled correctly
    public DateTimeOffset LocalAt(double rel)
    {
        var at = T0.AddTicks((long)Math.Round(rel * 1e7));
        return Info.FixedOffset ? at : TimeZoneInfo.ConvertTime(at, Zone);
    }
}

sealed class Template
{
    public static readonly (string Token, string Description)[] Tokens =
    {
        ("{datetime}", "Recording date and time (uses the label format)"),
        ("{date}", "Recording date"),
        ("{time}", "Recording time of day"),
        ("{elapsed}", "Time since the clip started"),
        ("{frame}", "Current frame number"),
        ("{frames}", "Total number of frames"),
        ("{tc}", "Camera timecode (HH:MM:SS:FF)"),
        ("{filename}", "File name"),
        ("{camera}", "Camera make and model (if recorded)"),
        ("{gps}", "GPS coordinates (if recorded)"),
        ("{resolution}", "Resolution, e.g. 3840x2160"),
        ("{fps}", "Frame rate"),
        ("{codec}", "Video codec"),
        ("{tz}", "UTC offset, e.g. UTC-07:00"),
    };

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Regex TokenRx = new(@"\{(\w+)(?::([^{}]*))?\}", RegexOptions.Compiled);
    static readonly HashSet<string> PerFrame = new(StringComparer.OrdinalIgnoreCase)
        { "datetime", "date", "time", "elapsed", "frame", "tc", "tz" };

    readonly List<(string? Literal, string? Token, string? Arg)> parts = new();

    // True if the text changes from frame to frame
    public bool IsDynamic { get; }

    public Template(string text)
    {
        int pos = 0;
        foreach (Match m in TokenRx.Matches(text))
        {
            if (m.Index > pos) parts.Add((text[pos..m.Index], null, null));
            var token = m.Groups[1].Value.ToLowerInvariant();
            parts.Add((m.Value, token, m.Groups[2].Success ? m.Groups[2].Value : null));
            if (PerFrame.Contains(token)) IsDynamic = true;
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) parts.Add((text[pos..], null, null));
    }

    public string Render(TemplateContext c, int frame, double rel)
    {
        var sb = new StringBuilder();
        foreach (var (literal, token, arg) in parts)
            sb.Append(token == null ? literal : Value(c, token, arg, frame, rel) ?? literal);
        return sb.ToString();
    }

    static string? Value(TemplateContext c, string token, string? arg, int frame, double rel)
    {
        var v = c.Info;
        return token switch
        {
            "datetime" => Fmt(c.LocalAt(rel), arg ?? c.Settings.DateFormat),
            "date" => Fmt(c.LocalAt(rel), arg ?? "yyyy-MM-dd"),
            "time" => Fmt(c.LocalAt(rel), arg ?? "HH:mm:ss.fff"),
            "elapsed" => Elapsed(rel),
            "frame" => (frame + 1).ToString(Inv),
            "frames" => c.Frames.ToString(Inv),
            "tc" => Smpte.Format(c.TcStart + frame, c.TcFps, c.TcDrop),
            "filename" => Path.GetFileName(v.Path),
            "camera" => v.Camera ?? "",
            "gps" => v.Gps ?? "",
            "resolution" => $"{v.Width}x{v.Height}",
            "fps" => v.Fps.ToString("0.###", Inv),
            "codec" => CodecName(v.Codec),
            "tz" => "UTC" + c.LocalAt(rel).ToString("zzz", Inv),
            _ => null,   // unknown tokens are shown as typed
        };
    }

    static string Fmt(DateTimeOffset t, string format)
    {
        try { return t.ToString(format, Inv); }
        catch (FormatException) { return "(invalid format)"; }
    }

    static string Elapsed(double s)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, s));
        return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    public static string CodecName(string codec) => codec switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "prores" => "ProRes",
        "mpeg2video" => "MPEG-2",
        "vp9" => "VP9",
        _ => codec.ToUpperInvariant(),
    };
}
