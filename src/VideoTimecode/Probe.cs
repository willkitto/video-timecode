using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VideoTimecode;

sealed class VideoInfo
{
    public string Path = "";
    public int Width, Height;              // as displayed (rotation applied)
    public string Codec = "", Profile = "", PixFmt = "", FormatName = "";
    public long VideoBitRate;               // bits per second, 0 if unknown
    public double Fps, Duration;
    public string? ColorRange, ColorSpace, ColorTransfer, ColorPrimaries;
    public string? Timecode, Camera, Gps;
    public List<double> FrameTimes = new();

    public DateTimeOffset Start;
    public bool FixedOffset;                // Start already carries the camera's own UTC offset
    public string StartSource = "";

    public string PixClass => Pix.ClassOf(PixFmt);
}

static class Probe
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<VideoInfo> ReadAsync(string path, CancellationToken ct)
    {
        var r = await Ffmpeg.RunAsync(Ffmpeg.FfprobePath!,
            new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }, ct);
        if (r.Code != 0) throw new Exception("Could not read the file: " + ErrorLine(r.Stderr));

        using var doc = JsonDocument.Parse(r.Stdout);
        var root = doc.RootElement;
        var format = root.GetProperty("format");
        JsonElement? video = null;
        long audioRate = 0;
        string? timecode = null;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = Str(s, "codec_type");
            bool coverArt = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
            if (type == "video" && video == null && !coverArt) video = s;
            if (type == "audio") audioRate += Long(s, "bit_rate");
            timecode ??= Tag(s, "timecode");
        }
        if (video is not { } v) throw new Exception("No video stream found.");

        var info = new VideoInfo
        {
            Path = path,
            Codec = Str(v, "codec_name"),
            Profile = Str(v, "profile"),
            PixFmt = Str(v, "pix_fmt"),
            FormatName = Str(format, "format_name"),
            Duration = Dbl(format, "duration"),
            ColorRange = Known(Str(v, "color_range")),
            ColorSpace = Known(Str(v, "color_space")),
            ColorTransfer = Known(Str(v, "color_transfer")),
            ColorPrimaries = Known(Str(v, "color_primaries")),
            Timecode = timecode ?? Tag(format, "timecode"),
        };

        int w = Int(v, "width"), h = Int(v, "height");
        if (Math.Abs(Rotation(v)) % 180 == 90) (w, h) = (h, w);
        info.Width = w;
        info.Height = h;

        info.Fps = Rate(Str(v, "avg_frame_rate"));
        if (info.Fps <= 0) info.Fps = Rate(Str(v, "r_frame_rate"));

        info.VideoBitRate = Long(v, "bit_rate");
        if (info.VideoBitRate <= 0)
        {
            long total = Long(format, "bit_rate");
            if (total > 0) info.VideoBitRate = Math.Max(0, total - audioRate);
        }

        var tags = Tags(format);
        foreach (var kv in Tags(v)) tags.TryAdd(kv.Key, kv.Value);
        ReadSidecarAndTags(info, tags);

        info.FrameTimes = await FrameTimesAsync(path, ct);
        if (info.FrameTimes.Count == 0) throw new Exception("No video frames found.");
        if (info.Duration <= 0) info.Duration = info.FrameTimes[^1] - info.FrameTimes[0];
        return info;
    }

    // Recording start (in order of preference), camera model and location
    static void ReadSidecarAndTags(VideoInfo info, Dictionary<string, string> tags)
    {
        bool found = false;

        // 1. Sony sidecar XML: local time with the camera's own UTC offset
        var xml = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(info.Path)!,
            System.IO.Path.GetFileNameWithoutExtension(info.Path) + "M01.XML");
        if (File.Exists(xml))
        {
            try
            {
                var x = XDocument.Load(xml);
                var created = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "CreationDate")?.Attribute("value")?.Value;
                if (created != null && DateTimeOffset.TryParse(created, Inv, DateTimeStyles.None, out var dto))
                {
                    info.Start = dto;
                    info.FixedOffset = true;
                    info.StartSource = "Sony sidecar " + System.IO.Path.GetFileName(xml);
                    found = true;
                }
                var dev = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Device");
                if (dev != null)
                    info.Camera = $"{dev.Attribute("manufacturer")?.Value} {dev.Attribute("modelName")?.Value}".Trim();
            }
            catch { }
        }

        // 2. Apple local-time tag (has offset)
        if (!found && tags.TryGetValue("com.apple.quicktime.creationdate", out var apple)
            && DateTimeOffset.TryParse(apple, Inv, DateTimeStyles.None, out var a))
        {
            info.Start = a;
            info.FixedOffset = true;
            info.StartSource = "Apple creation date";
            found = true;
        }

        // 3. Standard creation_time (UTC)
        if (!found && tags.TryGetValue("creation_time", out var ct)
            && DateTimeOffset.TryParse(ct, Inv, DateTimeStyles.AssumeUniversal, out var u))
        {
            info.Start = u.ToUniversalTime();
            info.FixedOffset = false;
            info.StartSource = "creation_time (UTC)";
            found = true;
        }
        if (!found) throw new Exception("No recording date found in the file.");

        info.Camera ??= JoinNonEmpty(
                            Get(tags, "com.apple.quicktime.make") ?? Get(tags, "com.android.manufacturer") ?? Get(tags, "make"),
                            Get(tags, "com.apple.quicktime.model") ?? Get(tags, "com.android.model") ?? Get(tags, "model"));

        var iso6709 = Get(tags, "com.apple.quicktime.location.iso6709") ?? Get(tags, "location") ?? Get(tags, "location-eng");
        if (iso6709 != null)
        {
            var m = Regex.Match(iso6709, @"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)");
            if (m.Success)
                info.Gps = $"{double.Parse(m.Groups[1].Value, Inv).ToString("0.00000", Inv)}, {double.Parse(m.Groups[2].Value, Inv).ToString("0.00000", Inv)}";
        }
    }

    // Presentation time of every frame; packet timestamps need no decoding, so this is fast
    static async Task<List<double>> FrameTimesAsync(string path, CancellationToken ct)
    {
        var times = new List<double>();
        var r = await Ffmpeg.RunAsync(Ffmpeg.FfprobePath!,
            new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path }, ct);
        foreach (var line in r.Stdout.Split('\n'))
        {
            var s = line.Trim().TrimEnd(',');
            if (double.TryParse(s, NumberStyles.Float, Inv, out var t)) times.Add(t);
        }
        times.Sort();
        return times;
    }

    static string? Known(string s) => s is "" or "unknown" ? null : s;
    static string? Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    static string? JoinNonEmpty(string? a, string? b)
    {
        if (a != null && b != null && b.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return b;
        var s = $"{a} {b}".Trim();
        return s.Length > 0 ? s : null;
    }

    static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()) : "";
    static int Int(JsonElement e, string k) => int.TryParse(Str(e, k), NumberStyles.Integer, Inv, out var v) ? v : 0;
    static long Long(JsonElement e, string k) => long.TryParse(Str(e, k), NumberStyles.Integer, Inv, out var v) ? v : 0;
    static double Dbl(JsonElement e, string k) => double.TryParse(Str(e, k), NumberStyles.Float, Inv, out var v) ? v : 0;

    static double Rate(string r)
    {
        var p = r.Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var n) && double.TryParse(p[1], NumberStyles.Float, Inv, out var d) && d > 0)
            return n / d;
        return double.TryParse(r, NumberStyles.Float, Inv, out var x) ? x : 0;
    }

    static Dictionary<string, string> Tags(JsonElement e)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject()) d[p.Name] = p.Value.ToString();
        return d;
    }

    static string? Tag(JsonElement e, string key) => Tags(e).TryGetValue(key, out var v) ? v : null;

    static int Rotation(JsonElement v)
    {
        if (v.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var sd in list.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var rot) && rot.TryGetInt32(out var deg)) return deg;
        return int.TryParse(Tag(v, "rotate"), out var r) ? r : 0;
    }

    // ffmpeg's most useful message is usually its last one
    public static string ErrorLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "unknown error";
}
