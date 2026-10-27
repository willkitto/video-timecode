using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoTimecode;

// Everything needed to work out why a GPU isn't used, gathered into one text report
// that can be saved and sent along with a question
static class Diagnostics
{
    public static string ReportPath => Path.Combine(AppSettings.Dir, "diagnostics.txt");

    public static async Task<string> BuildAsync(IReadOnlyList<Worker>? workers, AppSettings settings, string? trial, CancellationToken ct)
    {
        var r = new StringBuilder();
        void Line(string s = "") => r.AppendLine(s);
        void Section(string title) { Line(); Line("== " + title + " " + new string('=', Math.Max(3, 74 - title.Length))); }

        var version = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString();
        Line($"Video Timecode {version} diagnostics, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line("Send this file along with your question. It lists this PC's graphics hardware and drivers, the FFmpeg");
        Line("in use, every GPU encoder test with FFmpeg's own error, and the last run's log (which names your video files).");

        var gpus = EncoderCaps.Gpus();
        var det = EncoderCaps.Last;
        var ff = await FfmpegInfoAsync(ct);
        var findings = Findings(gpus, det, workers, ff);

        Section("Findings");
        if (findings.Count == 0) Line("Nothing looks wrong.");
        foreach (var f in findings) Line("* " + f);

        Section("This PC");
        Line($"Windows:    {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Line($"CPU:        {Wmi("SELECT Name FROM Win32_Processor", "Name")} ({Environment.ProcessorCount} logical cores)");
        if (long.TryParse(Wmi("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory"), out var ram))
            Line($"Memory:     {ram / 1073741824.0:0.#} GB");
        Line($"Computer:   {Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem", "Manufacturer")} {Wmi("SELECT Model FROM Win32_ComputerSystem", "Model")}");
        var power = SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery)
            Line($"Power:      {(power.PowerLineStatus == PowerLineStatus.Online ? "plugged in" : "on battery")}, battery {power.BatteryLifePercent:P0}");
        Line($"Settings:   {AppSettings.Dir}");

        Section("Graphics adapters (as Windows lists them)");
        if (gpus.Count == 0) Line("Windows didn't list any (the query failed).");
        foreach (var g in gpus)
        {
            Line($"{g.Name}");
            Line($"    vendor {VendorName(g.VendorId)}, driver {g.DriverVersion} ({g.DriverDate}), status {g.Status}" +
                 (g.ErrorCode != 0 ? $", PROBLEM CODE {g.ErrorCode}: {ProblemText(g.ErrorCode)}" : "") +
                 (g.RamMB > 0 ? $", {g.RamMB} MB reported" : ""));
            Line($"    {g.PnpId}");
        }

        var smi = await NvidiaSmiAsync(ct);
        if (smi != null)
        {
            Section("nvidia-smi");
            Line(smi);
        }

        Section("FFmpeg");
        Line($"In use:     {Ffmpeg.FfmpegPath ?? "(none found)"}  - {Ffmpeg.Source()}");
        Line($"ffprobe:    {Ffmpeg.FfprobePath ?? "(none found)"}");
        var all = Ffmpeg.AllCandidates();
        if (all.Count > 1) Line("Also found: " + string.Join("\n            ", all.Where(p => !p.Equals(Ffmpeg.FfmpegPath, StringComparison.OrdinalIgnoreCase))));
        if (ff.Version != "") Line($"Version:    {ff.Version}");
        if (ff.Flags.Count > 0) Line($"Built with: {string.Join("  ", ff.Flags.Select(f => $"{f.Key} {(f.Value ? "yes" : "NO")}"))}");
        if (ff.HwAccels.Count > 0) Line($"Hardware decoders: {string.Join(", ", ff.HwAccels)}");
        if (ff.Error != null) Line($"Error:      {ff.Error}");

        Section("GPU encoder check" + (det == null ? "" : det.FromCache ? $" (saved results from {det.When:yyyy-MM-dd HH:mm})" : $" ({det.When:yyyy-MM-dd HH:mm})"));
        if (EncoderCaps.LastError != null) Line("The check itself failed: " + EncoderCaps.LastError);
        if (det == null) Line("Not run yet (or the saved results come from an older version). Click Re-check GPUs.");
        else
        {
            Line($"FFmpeg:     {det.FfmpegPath}");
            Line($"Encoders in this build: {(det.BuiltEncoders.Count > 0 ? string.Join(" ", det.BuiltEncoders) : "(couldn't list them)")}");
            Line("Adapters FFmpeg can open (Direct3D 11 numbering):");
            if (det.Adapters.Count == 0) Line("    none");
            foreach (var a in det.Adapters)
                Line($"    #{a.Index}  {a.VendorId}:{a.DeviceId}  {a.Name}{(a.Error != "" ? "  - can't open: " + a.Error : "")}");
            Line($"NVIDIA GPUs NVENC sees: {(det.NvencGpus.Count > 0 ? string.Join(", ", det.NvencGpus.Select((n, i) => $"#{i} {n}")) : "none")}");
            Line("Test encodes (640x360, 5 frames):");
            foreach (var t in det.Tests.OrderBy(t => t.Encoder.Split('_').Last()).ThenBy(t => t.Device).ThenBy(t => t.Encoder).ThenBy(t => t.Format))
            {
                bool ok = t.Result != "" && t.Error == "";
                Line($"    {(ok ? "OK  " : "FAIL")}  {t.Encoder,-11} {t.Format,-12} {t.Device,-10} {t.Seconds,5:0.0}s" +
                     (ok ? "" : $"  exit {t.Code}: {t.Error}"));
            }
        }

        Section("Encoders the app will use");
        if (workers == null) Line("Not known yet.");
        else
            foreach (var w in workers)
                Line($"{w.Name}  [id {w.Id}, {w.Slots} at a time{(w.DeviceArgs.Count > 0 ? ", " + string.Join(' ', w.DeviceArgs) : "")}]\n    can make: " +
                     (w.Caps.Count > 0 ? string.Join(", ", w.Caps.Select(c => $"{Template.CodecName(c.Split('|')[0])} {Pix.Describe(c.Split('|')[1])}")) : "nothing"));

        Section("Current settings");
        Line($"Output {settings.Mode}; codec {settings.Codec}; quality {settings.Quality}; " +
             $"encoders {(settings.Encoders.Count == 0 ? "automatic" : string.Join(", ", settings.Encoders))}; " +
             $"parallel {(settings.ParallelJobs > 0 ? settings.ParallelJobs.ToString() : "automatic")}");

        if (trial != null)
        {
            Section("Encoder test on a video");
            Line(trial.TrimEnd());
        }

        AppendFile(r, "Last run log", Path.Combine(AppSettings.Dir, "last-run.log"), 400);
        AppendFile(r, "Error log (most recent)", Path.Combine(AppSettings.Dir, "error.log"), 80);
        return r.ToString();
    }

    // Plain-English explanations of what's wrong, worked out from everything gathered
    static List<string> Findings(List<GpuInfo> gpus, Detection? det, IReadOnlyList<Worker>? workers, FfmpegInfo ff)
    {
        var f = new List<string>();
        if (Ffmpeg.FfmpegPath == null) { f.Add("FFmpeg wasn't found. Click Re-download FFmpeg."); return f; }
        if (Ffmpeg.Source().StartsWith("found on the PATH"))
            f.Add($"The app is using an FFmpeg installed separately ({Ffmpeg.FfmpegPath}), not its own copy. If GPUs are missing, " +
                  "click Re-download FFmpeg: the app's own copy includes the NVIDIA, AMD and Intel encoders and is used ahead of the PATH.");
        foreach (var (flag, what) in new[] { ("nvenc", "NVIDIA"), ("amf", "AMD"), ("libvpl", "Intel") })
            if (ff.Flags.TryGetValue(flag, out var on) && !on && !(flag == "libvpl" && ff.Flags.GetValueOrDefault("libmfx")))
                f.Add($"This FFmpeg was built without {what} encoder support ({flag}). Click Re-download FFmpeg to get a build that has it.");

        foreach (var g in gpus)
        {
            if (g.ErrorCode != 0)
                f.Add($"Windows reports a problem with {g.Name}: code {g.ErrorCode}, {ProblemText(g.ErrorCode)}. Check it in Device Manager.");
            if (g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                f.Add($"\"{g.Name}\" means a graphics card has no proper driver installed. Install the driver from the maker's website (nvidia.com, amd.com, intel.com).");
        }

        if (det == null || workers == null) return f;
        foreach (var vendor in new[] { Vendor.Nvidia, Vendor.Amd, Vendor.Intel })
        {
            var listed = gpus.Where(g => g.Vendor == vendor).ToList();
            var used = workers.Where(w => w.Vendor == vendor).ToList();
            if (listed.Count == 0) continue;
            var label = vendor switch { Vendor.Nvidia => "NVIDIA (NVENC)", Vendor.Amd => "AMD (AMF)", _ => "Intel (Quick Sync)" };
            if (used.Count >= listed.Count)
            {
                f.Add($"{label}: working - {string.Join(", ", used.Select(w => w.Name))}.");
                continue;
            }
            var names = string.Join(", ", listed.Select(g => g.Name));
            if (used.Count > 0)
                f.Add($"{label}: Windows lists {listed.Count} ({names}) but only {used.Count} works for encoding: {string.Join(", ", used.Select(w => w.Name))}.");
            else
                f.Add($"{label}: Windows lists {names}, but no encoder test on it worked.");
            if (det.Adapters.Count > 0 && !det.Adapters.Any(a => EncoderCaps.VendorOf(a.VendorId) == vendor))
                f.Add($"    FFmpeg couldn't open it through Direct3D at all: it may be switched off, disabled, or missing its driver.");
            var suffix = vendor switch { Vendor.Nvidia => "nvenc", Vendor.Amd => "amf", _ => "qsv" };
            var errors = det.Tests.Where(t => t.Encoder.EndsWith(suffix) && t.Error != "" && t.Format == "yuv420p" || t.Encoder == $"*_{suffix}")
                .Select(t => t.Error).Distinct().ToList();
            foreach (var hint in errors.SelectMany(Explain).Distinct()) f.Add("    " + hint);
            if (errors.Count > 0 && !errors.SelectMany(Explain).Any())
                f.Add($"    FFmpeg said: {errors[0]}");
        }
        if (workers.All(w => w.Vendor == Vendor.Cpu)) f.Add("No GPU encoder works, so burning in uses the CPU (slower, same quality).");
        if (workers.First(w => w.Vendor == Vendor.Cpu).Caps.Count < EncoderCaps.Cpu().Caps.Count)
            f.Add("This FFmpeg is missing some CPU encoders (libx264, libx265, libsvtav1 or prores_ks), so some formats can't be made.");
        return f;
    }

    // What an FFmpeg error message means, in plain words
    static IEnumerable<string> Explain(string err)
    {
        var need = Regex.Match(err, @"minimum required Nvidia driver for nvenc is (\S+)", RegexOptions.IgnoreCase);
        var api = Regex.Match(err, @"Required: (\S+) Found: (\S+)");
        if (need.Success || api.Success || err.Contains("nvenc API version", StringComparison.OrdinalIgnoreCase))
            yield return "The NVIDIA driver is too old for this FFmpeg" +
                         (need.Success ? $" (it needs driver {need.Groups[1].Value.TrimEnd('.')} or newer)" : "") +
                         (api.Success ? $" (needs NVENC API {api.Groups[1].Value}, the driver has {api.Groups[2].Value})" : "") +
                         ". Update it from nvidia.com/drivers or the NVIDIA app, then click Re-check GPUs.";
        else if (Regex.IsMatch(err, @"nvcuda\.dll|nvEncodeAPI64\.dll|Could not dynamically load CUDA|cuInit|CUDA_ERROR", RegexOptions.IgnoreCase))
            yield return "FFmpeg couldn't load the NVIDIA driver's encoder. The driver may be missing or broken, or the GPU switched off. Reinstall the NVIDIA driver.";
        else if (err.Contains("No capable devices found", StringComparison.OrdinalIgnoreCase) || err.Contains("No NVENC capable devices", StringComparison.OrdinalIgnoreCase))
            yield return "NVENC found no NVIDIA GPU with a video encoder. Some laptop GPUs (e.g. GeForce MX) have none, so they can't encode video.";
        else if (Regex.IsMatch(err, @"OpenEncodeSessionEx failed|incompatible client key|out of memory \(10\)", RegexOptions.IgnoreCase))
            yield return "The NVIDIA GPU refused another encoding session. Close other apps using it (OBS, screen recorders, streaming) and click Re-check GPUs.";
        else if (err.Contains("unsupported device", StringComparison.OrdinalIgnoreCase))
            yield return "This NVIDIA GPU is too old for this FFmpeg's NVENC.";

        if (err.Contains("amfrt64.dll", StringComparison.OrdinalIgnoreCase))
            yield return "AMD's encoder runtime (amfrt64.dll, part of the AMD Adrenalin driver) isn't installed. Install the full driver from amd.com/support; Windows Update's driver often leaves it out.";
        else if (Regex.IsMatch(err, @"\bAMF\b.*(not supported|failed|version)|AMF_", RegexOptions.IgnoreCase))
            yield return "AMD's encoder (AMF) refused. The AMD driver may be too old: update it from amd.com/support, then click Re-check GPUs.";

        if (Regex.IsMatch(err, @"MFX session|libvpl|MFX_ERR|Error initializing an internal MFX session", RegexOptions.IgnoreCase))
            yield return "Intel's encoder (Quick Sync) couldn't start. Update the Intel graphics driver from intel.com, then click Re-check GPUs.";

        if (err.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) || err.Contains("Not included in this FFmpeg build"))
            yield return "This FFmpeg build doesn't include that encoder. Click Re-download FFmpeg.";
        if (err.Contains("Timed out"))
            yield return "The test timed out (a laptop GPU can be slow to wake up). Click Re-check GPUs.";
    }

    sealed class FfmpegInfo
    {
        public string Version = "";
        public Dictionary<string, bool> Flags = new();
        public List<string> HwAccels = new();
        public string? Error;
    }

    static async Task<FfmpegInfo> FfmpegInfoAsync(CancellationToken ct)
    {
        var info = new FfmpegInfo();
        if (Ffmpeg.FfmpegPath == null) return info;
        try
        {
            var v = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath, new[] { "-hide_banner", "-version" }, ct, TimeSpan.FromSeconds(20));
            var lines = v.Stdout.Split('\n').Select(l => l.Trim()).ToList();
            info.Version = lines.FirstOrDefault() ?? "";
            var config = lines.FirstOrDefault(l => l.StartsWith("configuration:", StringComparison.Ordinal)) ?? "";
            if (config != "")
                foreach (var flag in new[] { "nvenc", "ffnvcodec", "cuda-llvm", "amf", "libvpl", "libmfx", "d3d11va", "libx264", "libx265", "libsvtav1", "libass" })
                    info.Flags[flag] = config.Contains("--enable-" + flag);
            var h = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath, new[] { "-hide_banner", "-hwaccels" }, ct, TimeSpan.FromSeconds(20));
            info.HwAccels = h.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.EndsWith(':')).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException) { info.Error = e.Message; }
        return info;
    }

    static async Task<string?> NvidiaSmiAsync(CancellationToken ct)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(exe)) return null;
        try
        {
            var r = await Ffmpeg.RunAsync(exe, new[] { "--query-gpu=index,name,driver_version,pstate,encoder.stats.sessionCount,utilization.encoder",
                "--format=csv" }, ct, TimeSpan.FromSeconds(20));
            return (r.Stdout + r.Stderr).Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException) { return "couldn't run: " + e.Message; }
    }

    static void AppendFile(StringBuilder r, string title, string path, int lastLines)
    {
        if (!File.Exists(path)) return;
        r.AppendLine().AppendLine($"== {title} ({path}) " + new string('=', 20));
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length > lastLines) r.AppendLine($"(last {lastLines} of {lines.Length} lines)");
            foreach (var l in lines.TakeLast(lastLines)) r.AppendLine(l);
        }
        catch (Exception e) { r.AppendLine("couldn't read it: " + e.Message); }
    }

    static string Wmi(string query, string property)
    {
        try
        {
            using var q = new ManagementObjectSearcher(query);
            return string.Join(", ", q.Get().Cast<ManagementObject>().Select(m => (m[property]?.ToString() ?? "").Trim()).Where(s => s != ""));
        }
        catch { return "?"; }
    }

    static string VendorName(string id) => EncoderCaps.VendorOf(id)?.ToString() ?? (id == "" ? "unknown" : id);

    // Device Manager's problem codes that come up with graphics cards
    static string ProblemText(int code) => code switch
    {
        10 => "the device can't start",
        12 => "not enough free resources",
        14 => "a restart is needed",
        18 => "the driver needs reinstalling",
        22 => "the device is disabled",
        24 => "the device isn't present or not working",
        28 => "no driver is installed",
        31 => "Windows can't load the driver",
        43 => "Windows stopped the device after it reported problems",
        45 => "the device isn't connected",
        _ => "see Device Manager",
    };

    // GPUs Windows lists that the app can't encode with, for the status line
    public static List<string> UnusedGpus(IReadOnlyList<Worker> workers)
    {
        var gpus = EncoderCaps.Gpus().Where(g => g.Vendor != null).ToList();
        var unused = new List<string>();
        foreach (var group in gpus.GroupBy(g => g.Vendor))
        {
            var mine = workers.Where(w => w.Vendor == group.Key).ToList();
            var byName = group.Where(g => !mine.Any(w => w.Name.StartsWith(g.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            // Names can differ between Windows and the encoder; then go by how many work
            unused.AddRange((byName.Count + mine.Count == group.Count() ? byName : group.Skip(mine.Count)).Select(g => g.Name));
        }
        return unused;
    }
}
