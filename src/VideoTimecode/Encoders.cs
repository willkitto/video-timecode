using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    public List<string> DeviceArgs { get; set; } = new();   // picks one of several AMD or Intel GPUs

    [JsonIgnore] public int Active;

    public bool Supports(string codec, string cls) => Caps.Contains($"{codec}|{cls}");
    public override string ToString() => Name;
}

// A graphics adapter as FFmpeg's Direct3D 11 sees it (the numbering AMD and Intel GPUs are picked by)
sealed class Adapter
{
    public int Index { get; set; }
    public string VendorId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Error { get; set; } = "";
}

// One trial encode made while checking the encoders
sealed class EncoderTest
{
    public string Encoder { get; set; } = "";
    public string Format { get; set; } = "";
    public string Device { get; set; } = "";
    public string Result { get; set; } = "";   // pixel class produced, "" if it failed
    public int Code { get; set; }
    public string Error { get; set; } = "";
    public double Seconds { get; set; }
}

// What the encoder check found and why, for the diagnostics report
sealed class Detection
{
    public DateTime When { get; set; }
    public string FfmpegPath { get; set; } = "";
    public List<string> BuiltEncoders { get; set; } = new();
    public List<Adapter> Adapters { get; set; } = new();
    public List<string> NvencGpus { get; set; } = new();
    public List<EncoderTest> Tests { get; set; } = new();
    [JsonIgnore] public bool FromCache;
}

// A graphics adapter as Windows lists it
sealed record GpuInfo(string Name, string VendorId, string DriverVersion, string DriverDate, string PnpId, int ErrorCode, string Status, long RamMB)
{
    public Vendor? Vendor => EncoderCaps.VendorOf(VendorId);
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
    static readonly string[] CpuEncoders = { "libx264", "libx265", "libsvtav1", "prores_ks" };

    static string CachePath => Path.Combine(AppSettings.Dir, "encoders.json");

    // The last check's details, and the error if it crashed
    public static Detection? Last { get; private set; }
    public static string? LastError { get; set; }

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
    // convert, e.g. 10-bit down to 8-bit). Results are cached until the GPUs, their drivers
    // or ffmpeg change; force skips the cache. Every test is kept for the diagnostics report.
    public static async Task<List<Worker>> DetectAsync(Action<string>? status, CancellationToken ct, bool force = false)
    {
        var gpus = Gpus();
        var ff = new FileInfo(Ffmpeg.FfmpegPath!);
        // (Windows' per-app GPU preference reorders the adapters FFmpeg sees, so it's part of the key)
        var key = $"4|{ff.FullName}|{ff.Length}|{ff.LastWriteTimeUtc.Ticks}|{GpuPreference(ff.FullName)}|" +
                  string.Join(";", gpus.Select(g => $"{g.PnpId}|{g.DriverVersion}|{g.ErrorCode}"));
        LastError = null;
        try
        {
            if (!force && File.Exists(CachePath))
            {
                var cached = JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath), Json);
                if (cached?.Key == key && cached.Workers.Count > 0)
                {
                    Last = cached.Detection;
                    if (Last != null) Last.FromCache = true;
                    return cached.Workers;
                }
            }
        }
        catch { }

        status?.Invoke("Testing GPU encoders on this PC...");
        var d = new Detection { When = DateTime.Now, FfmpegPath = ff.FullName };
        using var gate = new SemaphoreSlim(6);
        d.BuiltEncoders = await BuiltEncodersAsync(ct);
        d.Adapters = await AdaptersAsync(gate, ct);

        var workers = new List<Worker>();
        foreach (var hw in Hardware)
        {
            if (!d.BuiltEncoders.Any(e => e.EndsWith("_" + hw.Suffix, StringComparison.Ordinal)))
            {
                d.Tests.Add(new EncoderTest { Encoder = $"*_{hw.Suffix}", Code = -1, Error = "Not included in this FFmpeg build." });
                continue;
            }

            // The GPUs to try: NVENC numbers NVIDIA GPUs itself; AMD and Intel GPUs are always opened
            // by adapter, since FFmpeg's default adapter is whichever Windows lists first (often an
            // NVIDIA card, which AMF then fails on). Only when the adapters couldn't be listed is the default tried.
            var devices = new List<(int Index, string Name, string[] Args, string Label)>();
            if (hw.Vendor == Vendor.Nvidia)
            {
                d.NvencGpus = await NvencGpusAsync(d.BuiltEncoders, ct);
                var names = gpus.Where(g => g.Vendor == Vendor.Nvidia).Select(g => g.Name).ToList();
                for (int i = 0; i < Math.Max(1, d.NvencGpus.Count); i++)
                    devices.Add((i, i < d.NvencGpus.Count ? d.NvencGpus[i] : i < names.Count ? names[i] : $"NVIDIA GPU {i}",
                        new[] { "-gpu", i.ToString() }, $"-gpu {i}"));
            }
            else
            {
                var ads = d.Adapters.Where(a => VendorOf(a.VendorId) == hw.Vendor).ToList();
                if (ads.Count > 0)
                    foreach (var a in ads) devices.Add((a.Index, a.Name, DeviceArgs(hw.Vendor, a.Index), $"adapter {a.Index}"));
                else if (d.Adapters.Count > 0 && !gpus.Any(g => g.Vendor == hw.Vendor))
                {
                    d.Tests.Add(new EncoderTest { Encoder = $"*_{hw.Suffix}", Code = -1, Error = $"Not tested: no {hw.Vendor} GPU on this PC." });
                    continue;
                }
                else
                    devices.Add((ads.FirstOrDefault()?.Index ?? 0,
                        ads.FirstOrDefault()?.Name ?? gpus.FirstOrDefault(g => g.Vendor == hw.Vendor)?.Name ?? $"{hw.Vendor} GPU",
                        Array.Empty<string>(), "default"));
            }

            foreach (var dev in devices)
            {
                var present = await Task.WhenAll(
                    TestAsync(d, $"h264_{hw.Suffix}", "yuv420p", dev.Args, dev.Label, gate, ct),
                    TestAsync(d, $"hevc_{hw.Suffix}", "yuv420p", dev.Args, dev.Label, gate, ct));
                if (present.All(p => p == null)) continue;

                var tests = (from codec in Codecs from f in TestFormats select (codec, f.Cls, f.Fmt)).ToList();
                var results = await Task.WhenAll(tests.Select(t =>
                    t.Cls == "420-8" && t.codec == "h264" ? Task.FromResult(present[0]) :
                    t.Cls == "420-8" && t.codec == "hevc" ? Task.FromResult(present[1]) :
                    TestAsync(d, $"{t.codec}_{hw.Suffix}", t.Fmt, dev.Args, dev.Label, gate, ct)));
                var caps = tests.Where((t, i) => results[i] == t.Cls).Select(t => $"{t.codec}|{t.Cls}").ToList();
                if (caps.Count == 0) continue;

                workers.Add(new Worker
                {
                    Id = hw.Vendor == Vendor.Nvidia ? $"nvenc:{dev.Index}" : devices.Count > 1 ? $"{hw.Suffix}:{dev.Index}" : hw.Suffix,
                    Name = $"{dev.Name} ({hw.Label})",
                    Vendor = hw.Vendor,
                    GpuIndex = dev.Index,
                    Slots = hw.Slots,
                    Caps = caps,
                    DeviceArgs = hw.Vendor == Vendor.Nvidia ? new() : dev.Args.ToList(),
                });
            }
        }

        // Identical cards get numbered, so they can be told apart in the list
        foreach (var same in workers.GroupBy(w => w.Name).Where(g => g.Count() > 1))
            foreach (var (w, i) in same.Select((w, i) => (w, i)))
                w.Name = w.Name.Insert(w.Name.LastIndexOf(" (", StringComparison.Ordinal), $" #{i + 1}");

        // The CPU only claims what this FFmpeg build can actually encode
        var cpu = Cpu();
        if (d.BuiltEncoders.Count > 0)
            cpu.Caps = cpu.Caps.Where(c => d.BuiltEncoders.Contains(EncodePlan.EncoderName(c.Split('|')[0], cpu))).ToList();
        workers.Add(cpu);

        Last = d;
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new Cache { Key = key, Workers = workers, Detection = d }, Json));
        }
        catch { }
        return workers;
    }

    // Windows' "Graphics settings" choice for an exe ("GpuPreference=1;" etc.), or "" if none
    static string GpuPreference(string exe)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            return k?.GetValue(exe) as string ?? "";
        }
        catch { return ""; }
    }

    // Arguments that open one particular AMD or Intel GPU (adapter numbers as Direct3D 11 counts them).
    // The encoders pick the device up by its type.
    static string[] DeviceArgs(Vendor v, int adapter) => v == Vendor.Intel
        ? new[] { "-init_hw_device", $"d3d11va=vtcd:{adapter}", "-init_hw_device", "qsv=vtc@vtcd" }
        : new[] { "-init_hw_device", $"d3d11va=vtcd:{adapter}" };

    // Returns the pixel-format class the encoder actually produced, or null if it failed
    static async Task<string?> TestAsync(Detection d, string encoder, string fmt, string[] device, string label, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        var tmp = Path.Combine(Path.GetTempPath(), $"vtc_cap_{Guid.NewGuid():N}.mkv");
        var test = new EncoderTest { Encoder = encoder, Format = fmt, Device = label };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            bool global = device.Length > 0 && device[0] == "-init_hw_device";
            var args = new List<string> { "-hide_banner", "-v", "error", "-y" };
            if (global) args.AddRange(device);
            args.AddRange(new[] { "-f", "lavfi", "-i", $"testsrc2=s=640x360:r=30:d=0.5,format={fmt}", "-frames:v", "5" });
            if (!global) args.AddRange(device);
            args.AddRange(new[] { "-c:v", encoder, tmp });
            // Generous timeout: a laptop's GPU can take a while to wake up
            var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath!, args, ct, TimeSpan.FromSeconds(30));
            test.Code = r.Code;
            if (r.Code != 0 || !File.Exists(tmp))
            {
                test.Error = Ffmpeg.Brief(r.Stderr, 4);
                return null;
            }
            var p = await Ffmpeg.RunAsync(Ffmpeg.FfprobePath!,
                new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt", "-of", "csv=p=0", tmp }, ct, TimeSpan.FromSeconds(20));
            var pixFmt = p.Stdout.Trim().TrimEnd(',');
            test.Result = Pix.ClassOf(pixFmt);
            if (test.Result != Pix.ClassOf(fmt)) test.Error = $"Came out as {pixFmt}, not {fmt}.";
            return test.Result;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            test.Code = -1;
            test.Error = e.Message;
            return null;
        }
        finally
        {
            test.Seconds = sw.Elapsed.TotalSeconds;
            lock (d.Tests) d.Tests.Add(test);
            gate.Release();
            try { File.Delete(tmp); } catch { }
        }
    }

    // The hardware encoders, and the CPU encoders the app uses, that this FFmpeg build includes
    static async Task<List<string>> BuiltEncodersAsync(CancellationToken ct)
    {
        var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath!, new[] { "-hide_banner", "-encoders" }, ct, TimeSpan.FromSeconds(20));
        return r.Stdout.Split('\n')
            .Select(l => Regex.Match(l, @"^\s*V\S{5}\s+(\S+)"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Where(n => n.EndsWith("_nvenc") || n.EndsWith("_amf") || n.EndsWith("_qsv") || CpuEncoders.Contains(n))
            .ToList();
    }

    // The adapters FFmpeg can open, numbered as it numbers them
    static async Task<List<Adapter>> AdaptersAsync(SemaphoreSlim gate, CancellationToken ct)
    {
        var found = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath!, new[] { "-hide_banner", "-v", "verbose", "-init_hw_device", $"d3d11va=d:{i}",
                    "-f", "lavfi", "-i", "nullsrc=s=64x64", "-frames:v", "1", "-f", "null", "-" }, ct, TimeSpan.FromSeconds(20));
                var m = Regex.Match(r.Stderr, @"Using device ([0-9a-fA-F]{4}):([0-9a-fA-F]{4}) \((.*)\)\.");
                if (!m.Success) return null;
                return new Adapter
                {
                    Index = i,
                    VendorId = m.Groups[1].Value.ToLowerInvariant(),
                    DeviceId = m.Groups[2].Value.ToLowerInvariant(),
                    Name = m.Groups[3].Value.Trim(),
                    Error = r.Code == 0 ? "" : Ffmpeg.Brief(r.Stderr, 2),
                };
            }
            finally { gate.Release(); }
        }));
        // Numbers run without gaps, so stop at the first that doesn't exist. Windows' software
        // renderer (vendor 1414) comes last and can't encode.
        return found.TakeWhile(a => a != null).Select(a => a!).Where(a => a.VendorId != "1414").ToList();
    }

    // NVENC's own numbering of NVIDIA GPUs (what -gpu N picks)
    static async Task<List<string>> NvencGpusAsync(List<string> built, CancellationToken ct)
    {
        var enc = built.FirstOrDefault(e => e.EndsWith("_nvenc", StringComparison.Ordinal));
        if (enc == null) return new();
        var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath!, new[] { "-hide_banner", "-f", "lavfi", "-i", "nullsrc=s=64x64",
            "-frames:v", "1", "-c:v", enc, "-gpu", "list", "-f", "null", "-" }, ct, TimeSpan.FromSeconds(30));
        return Regex.Matches(r.Stderr, @"GPU #(\d+) - < (.+?) >")
            .OrderBy(m => int.Parse(m.Groups[1].Value)).Select(m => m.Groups[2].Value.Trim()).ToList();
    }

    public static Vendor? VendorOf(string vendorId) => vendorId.ToLowerInvariant() switch
    {
        "10de" => Vendor.Nvidia,
        "1002" or "1022" => Vendor.Amd,
        "8086" => Vendor.Intel,
        _ => null,
    };

    // Graphics adapters as Windows lists them, including disabled ones and ones without a driver
    public static List<GpuInfo> Gpus()
    {
        try
        {
            using var q = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, DriverVersion, DriverDate, ConfigManagerErrorCode, Status, AdapterRAM FROM Win32_VideoController");
            return q.Get().Cast<ManagementObject>().Select(m =>
            {
                var pnp = m["PNPDeviceID"] as string ?? "";
                var ven = Regex.Match(pnp, @"VEN_([0-9A-Fa-f]{4})");
                var date = m["DriverDate"] as string ?? "";
                return new GpuInfo(
                    m["Name"] as string ?? "(no name)",
                    ven.Success ? ven.Groups[1].Value.ToLowerInvariant() : "",
                    m["DriverVersion"] as string ?? "",
                    date.Length >= 8 ? $"{date[..4]}-{date[4..6]}-{date[6..8]}" : "",
                    pnp,
                    m["ConfigManagerErrorCode"] is uint e ? (int)e : 0,
                    m["Status"] as string ?? "",
                    m["AdapterRAM"] is uint ram ? ram / 1048576 : 0);
            }).ToList();
        }
        catch { return new(); }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    sealed class Cache
    {
        public string Key { get; set; } = "";
        public List<Worker> Workers { get; set; } = new();
        public Detection? Detection { get; set; }
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
        // The chosen AMD or Intel GPU: open that one, decode on it, and the encoder uses it too
        Vendor.Intel or Vendor.Amd when w.DeviceArgs.Count > 0 =>
            w.DeviceArgs.Concat(new[] { "-hwaccel", "d3d11va", "-hwaccel_device", "vtcd" }).ToList(),
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
