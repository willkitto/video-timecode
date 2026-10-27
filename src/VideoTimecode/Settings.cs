using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoTimecode;

public enum OutputMode { Subs, Embed, Burn }
public enum EdgeStyle { Outline, Box }
public enum CreationMarks { Start, End }
public enum CodecChoice { MatchSource, H264, Hevc, Av1 }
public enum QualityChoice { MatchSource, Higher, Smaller }

// Values are the ASS numpad alignment codes
public enum Anchor9
{
    BottomLeft = 1, BottomCenter = 2, BottomRight = 3,
    MiddleLeft = 4, Center = 5, MiddleRight = 6,
    TopLeft = 7, TopCenter = 8, TopRight = 9,
}

public sealed class Overlay
{
    public bool Enabled { get; set; } = true;
    public Anchor9 Position { get; set; } = Anchor9.BottomRight;
    public string Text { get; set; } = "{datetime}";
    public double Size { get; set; } = 5;   // percent of video height
    public string Color { get; set; } = "#FFFFFF";

    // Per-overlay font; null uses the Style font / bold setting
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FontName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Bold { get; set; }

    public Overlay Clone() => (Overlay)MemberwiseClone();
}

public sealed class AppSettings
{
    public OutputMode Mode { get; set; } = OutputMode.Subs;
    public List<Overlay> Overlays { get; set; } = new() { new Overlay() };

    public string FontName { get; set; } = "Consolas";
    public bool Bold { get; set; } = true;
    public EdgeStyle Edge { get; set; } = EdgeStyle.Outline;
    public string EdgeColor { get; set; } = "#000000";
    public int BoxOpacity { get; set; } = 60;

    public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";
    public CreationMarks CreationMarks { get; set; } = CreationMarks.Start;
    public string TimeZoneId { get; set; } = "";   // empty = automatic

    public CodecChoice Codec { get; set; } = CodecChoice.MatchSource;
    public QualityChoice Quality { get; set; } = QualityChoice.MatchSource;
    // Worker ids to share burn-in jobs between, e.g. "nvenc:0", "qsv", "cpu"; empty = automatic
    public List<string> Encoders { get; set; } = new();
    public int ParallelJobs { get; set; }          // 0 = automatic

    // Before 2.2 a single encoder id ("auto", "cpu", "nvenc:0", ...) was stored here
    [JsonPropertyName("Encoder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyEncoder { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // VIDEO_TIMECODE_HOME overrides where settings, logs and the downloaded FFmpeg live
    public static string Dir => Environment.GetEnvironmentVariable("VIDEO_TIMECODE_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoTimecode");
    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static bool Exists => File.Exists(FilePath) || File.Exists(LegacyPath);

    // Choices saved by the PowerShell version, which kept them next to the script
    static string LegacyPath => Path.Combine(AppContext.BaseDirectory, "VideoTimecode.settings.json");

    public static AppSettings Load()
    {
        return File.Exists(FilePath) ? LoadFile(FilePath) ?? new() : ImportLegacy() ?? new();
    }

    public void Save() => SaveFile(FilePath);

    public static AppSettings? LoadFile(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json)?.Upgrade(); }
        catch { return null; }
    }

    AppSettings Upgrade()
    {
        if (Encoders.Count == 0 && LegacyEncoder is { Length: > 0 } e && e != "auto") Encoders = new() { e };
        LegacyEncoder = null;
        return this;
    }

    public void SaveFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.Overlays = Overlays.Select(o => o.Clone()).ToList();
        c.Encoders = new List<string>(Encoders);
        return c;
    }

    static AppSettings? ImportLegacy()
    {
        if (!File.Exists(LegacyPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LegacyPath));
            var r = doc.RootElement;
            string Str(string key, string fallback) =>
                r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

            var s = new AppSettings();
            s.Mode = Str("Mode", "Subs") switch { "Embed" => OutputMode.Embed, "Burn" => OutputMode.Burn, _ => OutputMode.Subs };
            s.FontName = Str("FontName", s.FontName);
            if (r.TryGetProperty("Bold", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False) s.Bold = b.GetBoolean();
            s.EdgeColor = Str("OutlineColor", s.EdgeColor);
            s.DateFormat = Str("Format", s.DateFormat);
            s.CreationMarks = Str("CreationMarks", "Start") == "End" ? CreationMarks.End : CreationMarks.Start;
            s.TimeZoneId = Str("TimeZone", "");

            var o = s.Overlays[0];
            o.Color = Str("FontColor", o.Color);
            if (r.TryGetProperty("FontSize", out var fs) && fs.TryGetDouble(out var size)) o.Size = size;
            if (Enum.TryParse<Anchor9>(Str("Position", "BottomRight"), out var pos)) o.Position = pos;
            return s;
        }
        catch { return null; }
    }
}
