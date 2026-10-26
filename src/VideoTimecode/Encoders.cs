using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoTimecode;

public enum Vendor { Nvidia, Amd, Intel, Cpu }

// Something that can encode: one GPU's hardware encoder, or the CPU
sealed class Worker
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Vendor Vendor { get; set; }
    public int GpuIndex { get; set; }
    public int Slots { get; set; } = 1;      // jobs it runs at once
    public List<string> Caps { get; set; } = new();   // "codec|class", e.g. "hevc|420-10"

    [JsonIgnore] public int Active;

    public bool Supports(string codec, string cls) => Caps.Contains($"{codec}|{cls}");
    public override string ToString() => Name;
}

static class EncoderCaps
{
    static readonly (Vendor Vendor, string Suffix, string Label, int Slots)[] Hardware =
    {
        (Vendor.Nvidia, "nvenc", "NVENC", 3),
        (Vendor.Amd, "amf", "AMF", 2),
        (Vendor.Intel, "qsv", "Quick Sync", 2),
    };
    static readonly string[] Codecs = { "h264", "hevc", "av1" };
    static readonly (string Cls, string Fmt)[] TestFormats =
        { ("420-8", "yuv420p"), ("420-10", "yuv420p10le"), ("422-8", "yuv422p"), ("422-10", "yuv422p10le") };

    static string CachePath => Path.Combine(AppSettings.Dir, "encoders.json");

    public static Worker Cpu() => new()
    {
        Id = "cpu",
        Name = "CPU",
        Vendor = Vendor.Cpu,
        Slots = 1,   // x264/x265 already use every core
        Caps = Pix.Classes.SelectMany(c => new[] { $"h264|{c}", $"hevc|{c}" })
            .Concat(new[] { "av1|420-8", "av1|420-10", "prores|422-10", "prores|444-10" }).ToList(),
    };

    // Finds every working hardware encoder on this PC by trying a tiny encode with each,
    // then checking the format that actually came out (ffmpeg would otherwise quietly
    // convert, e.g. 10-bit down to 8-bit). Results are cached until the GPUs or ffmpeg change.
    public static async Task<List<Worker>> DetectAsync(Action<string>? status, CancellationToken ct)
    {
        var gpus = GpuNames();
        var ff = new FileInfo(Ffmpeg.FfmpegPath!);
        var key = $"2|{ff.FullName}|{ff.Length}|{ff.LastWriteTimeUtc.Ticks}|{string.Join(";", gpus)}";
        try
        {
            if (File.Exists(CachePath))
            {
                var cached = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath), Json);
                if (cached?.Key == key && cached.Workers.Count > 0) return cached.Workers;
            }
        }
        catch { }

        status?.Invoke("Testing GPU encoders on this PC...");
        var workers = new List<Worker>();
        using var gate = new SemaphoreSlim(6);
        foreach (var hw in Hardware)
        {
            string[] gpu0 = hw.Vendor == Vendor.Nvidia ? new[] { "-gpu", "0" } : Array.Empty<string>();
            var present = await Task.WhenAll(
                TestAsync($"h264_{hw.Suffix}", "yuv420p", gpu0, gate, ct),
                TestAsync($"hevc_{hw.Suffix}", "yuv420p", gpu0, gate, ct));
            if (present.All(p => p == null)) continue;
            string probeCodec = present[0] != null ? "h264" : "hevc";

            var tests = (from codec in Codecs from f in TestFormats select (codec, f.Cls, f.Fmt)).ToList();
            var results = await Task.WhenAll(tests.Select(t => TestAsync($"{t.codec}_{hw.Suffix}", t.Fmt, gpu0, gate, ct)));
            var caps = tests.Where((t, i) => results[i] == t.Cls).Select(t => $"{t.codec}|{t.Cls}").ToList();
            if (caps.Count == 0) continue;

            // NVENC can address each NVIDIA GPU separately
            var indices = new List<int> { 0 };
            if (hw.Vendor == Vendor.Nvidia)
                for (int i = 1; i < 8; i++)
                {
                    if (await TestAsync($"{probeCodec}_nvenc", "yuv420p", new[] { "-gpu", i.ToString() }, gate, ct) == null) break;
                    indices.Add(i);
                }

            var names = gpus.Where(n => Matches(hw.Vendor, n)).ToList();
            foreach (var i in indices)
            {
                var gpuName = i < names.Count ? names[i] : $"{hw.Vendor} GPU {i}";
                workers.Add(new Worker
                {
                    Id = hw.Vendor == Vendor.Nvidia ? $"nvenc:{i}" : hw.Suffix,
                    Name = $"{gpuName} ({hw.Label})",
                    Vendor = hw.Vendor,
                    GpuIndex = i,
                    Slots = hw.Slots,
                    Caps = caps,
                });
            }
        }
        workers.Add(Cpu());

        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new Cache { Key = key, Workers = workers }, Json));
        }
        catch { }
        return workers;
    }

    // Returns the pixel-format class the encoder actually produced, or null if it failed
    static async Task<string?> TestAsync(string encoder, string fmt, string[] extra, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        var tmp = Path.Combine(Path.GetTempPath(), $"vtc_cap_{Guid.NewGuid():N}.mkv");
        try
        {
            var args = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=s=640x360:r=30:d=0.5,format={fmt}", "-frames:v", "5" };
            args.AddRange(extra);
            args.AddRange(new[] { "-c:v", encoder, tmp });
            var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath!, args, ct, TimeSpan.FromSeconds(20));
            if (r.Code != 0 || !File.Exists(tmp)) return null;
            var p = await Ffmpeg.RunAsync(Ffmpeg.FfprobePath!,
                new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt", "-of", "csv=p=0", tmp }, ct, TimeSpan.FromSeconds(20));
            return Pix.ClassOf(p.Stdout.Trim().TrimEnd(','));
        }
        finally
        {
            gate.Release();
            try { File.Delete(tmp); } catch { }
        }
    }

    static List<string> GpuNames()
    {
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            return q.Get().Cast<ManagementObject>().Select(m => m["Name"] as string).OfType<string>().ToList();
        }
        catch { return new(); }
    }

    static bool Matches(Vendor v, string gpuName) => v switch
    {
        Vendor.Nvidia => gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase),
        Vendor.Intel => gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase),
        Vendor.Amd => gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    sealed class Cache
    {
        public string Key { get; set; } = "";
        public List<Worker> Workers { get; set; } = new();
    }
}

// Decides what to encode to, and builds the ffmpeg arguments for a given encoder
static class EncodePlan
{
    // Target codec and pixel class for a source, falling back where nothing can encode it
    public static (string Codec, string Cls) Target(VideoInfo v, AppSettings s, IReadOnlyList<Worker> workers)
    {
        var cls = v.PixClass;
        if (cls == "") cls = "420-8";

        string codec = s.Codec switch
        {
            CodecChoice.H264 => "h264",
            CodecChoice.Hevc => "hevc",
            CodecChoice.Av1 => "av1",
            _ => v.Codec switch
            {
                "h264" or "hevc" or "av1" or "prores" => v.Codec,
                _ => cls == "420-8" ? "h264" : "hevc",   // e.g. MPEG-2, VP9, MJPEG
            },
        };
        if (codec == "prores") cls = cls.StartsWith("444") ? "444-10" : "422-10";

        if (!workers.Any(w => w.Supports(codec, cls))) codec = "hevc";   // e.g. AV1 4:2:2
        return (codec, cls);
    }

    public static string EncoderName(string codec, Worker w) => w.Vendor switch
    {
        Vendor.Nvidia => codec + "_nvenc",
        Vendor.Intel => codec + "_qsv",
        Vendor.Amd => codec + "_amf",
        _ => codec switch { "h264" => "libx264", "hevc" => "libx265", "av1" => "libsvtav1", _ => "prores_ks" },
    };

    public static List<string> DecodeArgs(Worker w) => w.Vendor switch
    {
        // Hardware decoding where possible; ffmpeg falls back to software for formats the GPU can't decode
        Vendor.Nvidia => new() { "-hwaccel", "cuda", "-hwaccel_device", w.GpuIndex.ToString() },
        Vendor.Intel or Vendor.Amd => new() { "-hwaccel", "d3d11va" },
        _ => new(),
    };

    public static List<string> VideoArgs(VideoInfo v, AppSettings s, string codec, string cls, Worker w, bool mp4Family)
    {
        var enc = EncoderName(codec, w);
        var a = new List<string> { "-c:v", enc };

        if (codec == "prores")
        {
            a.AddRange(new[] { "-profile:v", ProresProfile(v).ToString(), "-vendor", "apl0",
                "-pix_fmt", cls == "444-10" ? "yuva444p10le" : "yuv422p10le" });
            AddColor(a, v);
            return a;
        }

        switch (w.Vendor)
        {
            case Vendor.Nvidia: a.AddRange(new[] { "-gpu", w.GpuIndex.ToString(), "-preset", "p5", "-tune", "hq" }); break;
            case Vendor.Intel: a.AddRange(new[] { "-preset", "medium" }); break;
            case Vendor.Amd: a.AddRange(new[] { "-quality", "quality" }); break;
            default:
                a.AddRange(new[] { "-preset", codec == "av1" ? "6" : "medium", "-pix_fmt", Pix.SoftwareFormat(cls) });
                if (codec == "hevc") a.AddRange(new[] { "-x265-params", "log-level=error" });
                break;
        }

        double factor = s.Quality switch { QualityChoice.Higher => 1.5, QualityChoice.Smaller => 0.6, _ => 1.0 };
        if (v.VideoBitRate > 0)
        {
            long b = Math.Max(500_000, (long)(v.VideoBitRate * factor));
            a.AddRange(new[] { "-b:v", b.ToString() });
            if (enc != "libsvtav1") a.AddRange(new[] { "-maxrate", ((long)(b * 1.5)).ToString(), "-bufsize", (b * 2).ToString() });
            if (w.Vendor == Vendor.Nvidia) a.AddRange(new[] { "-rc", "vbr" });
            if (w.Vendor == Vendor.Amd) a.AddRange(new[] { "-rc", "vbr_peak" });
        }
        else
        {
            // Bitrate unknown: aim for high constant quality instead
            a.AddRange(w.Vendor switch
            {
                Vendor.Nvidia => new[] { "-rc", "vbr", "-cq", "19", "-b:v", "0" },
                Vendor.Intel => new[] { "-global_quality", "20" },
                Vendor.Amd => new[] { "-rc", "cqp", "-qp_i", "18", "-qp_p", "20" },
                _ => new[] { "-crf", codec switch { "hevc" => "20", "av1" => "30", _ => "18" } },
            });
        }

        if (codec == "hevc" && mp4Family) a.AddRange(new[] { "-tag:v", "hvc1" });   // plays on Apple devices
        AddColor(a, v);
        return a;
    }

    static void AddColor(List<string> a, VideoInfo v)
    {
        if (v.ColorPrimaries != null) a.AddRange(new[] { "-color_primaries", v.ColorPrimaries });
        if (v.ColorTransfer != null) a.AddRange(new[] { "-color_trc", v.ColorTransfer });
        if (v.ColorSpace != null) a.AddRange(new[] { "-colorspace", v.ColorSpace });
        if (v.ColorRange != null) a.AddRange(new[] { "-color_range", v.ColorRange });
    }

    static int ProresProfile(VideoInfo v) => v.Codec != "prores" ? 3 : v.Profile.Replace(" ", "").ToLowerInvariant() switch
    {
        "proxy" => 0,
        "lt" => 1,
        "standard" => 2,
        "4444" => 4,
        "4444xq" => 5,
        _ => 3,
    };
}
