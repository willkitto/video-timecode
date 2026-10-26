using System.Globalization;

namespace VideoTimecode;

sealed class Job
{
    public Job(string input, object tag) { Input = input; Tag = tag; }

    public string Input { get; }
    public object Tag { get; }               // the UI row for this file
    public VideoInfo? Info;
    public string? AssPath, TempDir, Output;
    public string Codec = "", Cls = "";
    public readonly HashSet<string> FailedWorkers = new();
    public bool ForceMkv, Succeeded, Failed;
}

sealed class JobRunner
{
    readonly AppSettings s;
    readonly IReadOnlyList<Worker> workers;
    readonly TimeZoneInfo zone;
    readonly Action<Job, string, double?> report;   // status text, progress 0..1
    readonly Action<string> log;

    public JobRunner(AppSettings settings, IReadOnlyList<Worker> workers, Action<Job, string, double?> report, Action<string> log)
    {
        s = settings;
        this.workers = workers;
        this.report = report;
        this.log = log;
        zone = string.IsNullOrEmpty(s.TimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId);
    }

    // Jobs that run at once: enough to keep a GPU busy (one job alone leaves it mostly idle),
    // but not so many that decoding and drawing the text starve the CPU
    public static int AutoParallel => Math.Clamp(Environment.ProcessorCount / 6, 1, 4);

    public async Task RunAsync(IReadOnlyList<Job> jobs, CancellationToken ct)
    {
        try
        {
            await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (job, t) =>
                {
                    try { await PrepareAsync(job, t); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { Fail(job, e.Message); }
                });

            var ready = jobs.Where(j => !j.Failed).ToList();
            if (s.Mode == OutputMode.Subs)
            {
                foreach (var j in ready) Done(j, "Done: " + Path.GetFileName(j.AssPath));
                return;
            }
            await ScheduleAsync(ready, ct);
        }
        finally
        {
            foreach (var j in jobs)
                if (j.TempDir != null) try { Directory.Delete(j.TempDir, true); } catch { }
        }
    }

    async Task PrepareAsync(Job job, CancellationToken ct)
    {
        report(job, "Reading...", null);
        var info = job.Info = await Probe.ReadAsync(job.Input, ct);
        var ctx = new TemplateContext(info, s, zone);

        if (s.Mode == OutputMode.Subs)
        {
            job.AssPath = Path.ChangeExtension(job.Input, ".ass");
        }
        else
        {
            job.TempDir = Path.Combine(Path.GetTempPath(), "vtc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job.TempDir);
            job.AssPath = Path.Combine(job.TempDir, "tc.ass");
        }
        AssWriter.Write(job.AssPath, ctx);

        var label = ctx.LocalAt(0);
        log($"{job.Input}\n  date from {info.StartSource}; first frame {label.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}; " +
            $"{info.Width}x{info.Height} {info.Codec} {info.PixFmt} {info.Fps:0.###} fps {info.VideoBitRate / 1000} kb/s, {ctx.Frames} frames");

        if (s.Mode == OutputMode.Burn)
        {
            (job.Codec, job.Cls) = EncodePlan.Target(info, s, workers);
            report(job, $"Waiting ({Template.CodecName(job.Codec)}, {Pix.Describe(job.Cls)})", 0);
        }
        else report(job, "Waiting", 0);
    }

    async Task ScheduleAsync(List<Job> pending, CancellationToken ct)
    {
        int maxParallel = s.ParallelJobs > 0 ? s.ParallelJobs : AutoParallel;
        var running = new Dictionary<Task<bool>, (Job Job, Worker? Worker)>();
        try
        {
            while (pending.Count > 0 || running.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (int k = 0; k < pending.Count && running.Count < maxParallel;)
                {
                    var job = pending[k];
                    Worker? w = null;
                    if (s.Mode == OutputMode.Burn)
                    {
                        var (free, impossible) = PickWorker(job);
                        if (impossible)
                        {
                            Fail(job, $"No encoder here can produce {Template.CodecName(job.Codec)} {Pix.Describe(job.Cls)}.");
                            pending.RemoveAt(k);
                            continue;
                        }
                        if (free == null) { k++; continue; }   // its encoder is busy; try the next file
                        w = free;
                        w.Active++;
                    }
                    pending.RemoveAt(k);
                    var task = w != null ? BurnAsync(job, w, ct) : EmbedAsync(job, ct);
                    running[task] = (job, w);
                }
                if (running.Count == 0) break;

                var finished = await Task.WhenAny(running.Keys);
                var (j, worker) = running[finished];
                running.Remove(finished);
                if (worker != null) worker.Active--;

                bool ok;
                try { ok = await finished; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { ok = false; report(j, "Failed: " + e.Message, null); }

                if (!ok && !ct.IsCancellationRequested)
                {
                    if (worker != null && CanRetry(j, worker))
                    {
                        report(j, "Retrying...", 0);
                        pending.Insert(0, j);
                    }
                    else j.Failed = true;
                }
            }
        }
        finally
        {
            // On cancel, let the killed ffmpeg processes finish exiting
            try { await Task.WhenAll(running.Keys); } catch { }
        }
    }

    // A free encoder for the job. GPUs are preferred; the CPU is used only when no GPU can
    // produce the format (measured: adding CPU encodes alongside GPU ones slows the batch).
    (Worker? Free, bool Impossible) PickWorker(Job job)
    {
        var eligible = workers.Where(w => w.Supports(job.Codec, job.Cls) && !job.FailedWorkers.Contains(w.Id));
        if (s.Encoder == "cpu")
            eligible = eligible.Where(w => w.Vendor == Vendor.Cpu);
        else if (s.Encoder != "auto")
        {
            var chosen = eligible.Where(w => w.Id == s.Encoder).ToList();
            eligible = chosen.Count > 0 ? chosen : eligible.Where(w => w.Vendor == Vendor.Cpu);
        }
        var list = eligible.ToList();
        if (list.Count == 0) return (null, true);

        var gpus = list.Where(w => w.Vendor != Vendor.Cpu).ToList();
        var pool = gpus.Count > 0 ? gpus : list;
        var free = pool.Where(w => w.Active < w.Slots)
            .OrderBy(w => (double)w.Active / w.Slots)
            .ThenBy(w => workers.ToList().IndexOf(w))
            .FirstOrDefault();
        return (free, false);
    }

    bool CanRetry(Job job, Worker failed)
    {
        if (failed.Vendor != Vendor.Cpu)
        {
            job.FailedWorkers.Add(failed.Id);   // try another GPU, then the CPU
            return !PickWorker(job).Impossible;
        }
        if (!job.ForceMkv && !IsMkv(job.Input))
        {
            job.ForceMkv = true;   // e.g. an audio format the original container type can't hold
            return true;
        }
        return false;
    }

    async Task<bool> BurnAsync(Job job, Worker w, CancellationToken ct)
    {
        var v = job.Info!;
        string ext = OutputExtension(job);
        bool mp4Family = ext is ".mp4" or ".mov";
        job.Output = Path.Combine(Path.GetDirectoryName(job.Input)!, Path.GetFileNameWithoutExtension(job.Input) + "_timecode_burned" + ext);

        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
        a.AddRange(EncodePlan.DecodeArgs(w));
        a.AddRange(new[] { "-i", job.Input, "-map", "0:v:0", "-map", "0:a?" });
        if (ext == ".mkv" && IsMkv(job.Input)) a.AddRange(new[] { "-map", "0:s?", "-c:s", "copy" });
        a.AddRange(new[] { "-vf", "subtitles=tc.ass", "-fps_mode", "passthrough" });
        a.AddRange(EncodePlan.VideoArgs(v, s, job.Codec, job.Cls, w, mp4Family));
        a.AddRange(new[] { "-c:a", "copy", "-map_metadata", "0", "-map_chapters", "0" });
        if (mp4Family)
        {
            a.AddRange(new[] { "-movflags", "+faststart+use_metadata_tags" });
            if (v.Timecode != null) a.AddRange(new[] { "-timecode", v.Timecode });
        }
        a.AddRange(new[] { "-max_muxing_queue_size", "4096", "-progress", "pipe:1", "-nostats", job.Output });

        var label = w.Vendor == Vendor.Cpu ? "CPU" : w.Name;
        return await RunFfmpegAsync(job, a, $"{EncodePlan.EncoderName(job.Codec, w)} on {label}", ct);
    }

    async Task<bool> EmbedAsync(Job job, CancellationToken ct)
    {
        job.Output = Path.Combine(Path.GetDirectoryName(job.Input)!, Path.GetFileNameWithoutExtension(job.Input) + "_timecode.mkv");
        var a = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", job.Input, "-i", job.AssPath!,
            "-map", "0:v", "-map", "0:a?", "-map", "1:0", "-c", "copy", "-c:s", "ass",
            "-metadata:s:s:0", "title=Timecode", "-metadata:s:s:0", "language=eng", "-disposition:s:0", "default",
            "-map_metadata", "0", "-map_chapters", "0", "-progress", "pipe:1", "-nostats", job.Output,
        };
        return await RunFfmpegAsync(job, a, "copying", ct);
    }

    async Task<bool> RunFfmpegAsync(Job job, List<string> args, string what, CancellationToken ct)
    {
        double duration = Math.Max(0.001, job.Info!.Duration);
        report(job, $"Starting ({what})", 0);
        log("  ffmpeg " + string.Join(' ', args.Select(x => x.Contains(' ') ? $"\"{x}\"" : x)));

        var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath!, args, ct, workDir: job.TempDir, lowPriority: true,
            onStdoutLine: line =>
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                if (long.TryParse(line.AsSpan(12), out var us) && us > 0)
                {
                    double f = Math.Clamp(us / 1e6 / duration, 0, 1);
                    report(job, $"{f:P0} ({what})", f);
                }
            });

        if (r.Code == 0)
        {
            Done(job, $"Done: {Path.GetFileName(job.Output)} ({what})");
            return true;
        }
        try { if (job.Output != null) File.Delete(job.Output); } catch { }
        var err = Probe.ErrorLine(r.Stderr);
        log($"  FAILED ({what}): {r.Stderr.Trim()}");
        report(job, $"Failed ({what}): {err}", null);
        return false;
    }

    string OutputExtension(Job job)
    {
        if (job.ForceMkv) return ".mkv";
        var ext = Path.GetExtension(job.Input).ToLowerInvariant();
        if (job.Codec == "prores") return ext == ".mkv" ? ".mkv" : ".mov";
        return ext switch { ".mp4" or ".m4v" => ".mp4", ".mov" => ".mov", _ => ".mkv" };
    }

    static bool IsMkv(string path) => Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    void Done(Job job, string message)
    {
        job.Succeeded = true;
        report(job, message, 1);
    }

    void Fail(Job job, string message)
    {
        job.Failed = true;
        log($"{job.Input}\n  FAILED: {message}");
        report(job, "Failed: " + message, null);
    }
}
