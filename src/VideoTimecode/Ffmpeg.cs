using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoTimecode;

public sealed record RunResult(int Code, string Stdout, string Stderr);

static class Ffmpeg
{
    public static string? FfmpegPath { get; private set; }
    public static string? FfprobePath { get; private set; }
    public static bool Ready => FfmpegPath != null && FfprobePath != null;

    static string ToolsDir => Path.Combine(AppSettings.Dir, "ffmpeg");

    public static bool Locate()
    {
        foreach (var dir in CandidateDirs())
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
            var ffprobe = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                FfmpegPath = ffmpeg;
                FfprobePath = ffprobe;
                return true;
            }
        }
        return false;
    }

    static IEnumerable<string> CandidateDirs()
    {
        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin");
        yield return Path.Combine(ToolsDir, "bin");
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            yield return d.Trim().Trim('"');
    }

    public static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default,
        TimeSpan? timeout = null, string? workDir = null, Action<string>? onStdoutLine = null, bool lowPriority = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (onStdoutLine != null) onStdoutLine(e.Data);
            else lock (stdout) stdout.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 200_000) stderr.Remove(0, stderr.Length - 100_000);
            }
        };

        p.Start();
        // Keep the desktop responsive while several encodes run
        if (lowPriority) try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) linked.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            await p.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
            return new RunResult(-1, stdout.ToString(), "Timed out.");
        }
        p.WaitForExit();   // lets the async readers drain
        return new RunResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // --- first-run download -------------------------------------------------

    const string BuildsApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
    const string Checksums = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256";

    // Downloads the newest stable shared GPL build from BtbN/FFmpeg-Builds, checks its
    // SHA-256 against the published checksums, and unpacks it into %LOCALAPPDATA%.
    public static async Task DownloadAsync(IProgress<(string Message, double Fraction)> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VideoTimecode/2.0");

        progress.Report(("Finding the latest FFmpeg build...", 0));
        using var release = JsonDocument.Parse(await http.GetStringAsync(BuildsApi, ct));
        string? name = null, url = null;
        var best = new Version(0, 0);
        foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
        {
            var n = asset.GetProperty("name").GetString() ?? "";
            var m = Regex.Match(n, @"^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-shared-[\d.]+\.zip$");
            if (!m.Success) continue;
            var v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            if (v > best) { best = v; name = n; url = asset.GetProperty("browser_download_url").GetString(); }
        }
        if (name == null || url == null) throw new Exception("Could not find an FFmpeg build to download.");

        var sums = await http.GetStringAsync(Checksums, ct);
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == name)?[0];
        if (expected == null) throw new Exception("FFmpeg checksum not published; refusing to install an unverified download.");

        var zip = Path.Combine(Path.GetTempPath(), name);
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report(($"Downloading FFmpeg {best.Major}.{best.Minor}... {done / 1048576} of {total / 1048576} MB", (double)done / total));
            }
        }

        progress.Report(("Verifying download...", 1));
        string actual;
        await using (var fs = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new Exception("The FFmpeg download failed its checksum check and was deleted.");
        }

        progress.Report(("Unpacking FFmpeg...", 1));
        var staging = ToolsDir + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        ZipFile.ExtractToDirectory(zip, staging);
        File.Delete(zip);
        var bin = Directory.GetDirectories(staging, "bin", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new Exception("The FFmpeg download did not contain a bin folder.");
        if (Directory.Exists(ToolsDir)) Directory.Delete(ToolsDir, true);
        Directory.CreateDirectory(ToolsDir);
        Directory.Move(bin, Path.Combine(ToolsDir, "bin"));
        var license = Directory.GetFiles(staging, "LICENSE*", SearchOption.AllDirectories).FirstOrDefault();
        if (license != null) File.Copy(license, Path.Combine(ToolsDir, "LICENSE.txt"), true);
        Directory.Delete(staging, true);

        if (!Locate()) throw new Exception("FFmpeg was downloaded but could not be found afterwards.");
    }
}
