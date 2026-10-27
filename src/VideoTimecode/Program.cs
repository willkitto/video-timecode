namespace VideoTimecode;

static class Program
{
    // Usage: VideoTimecode.exe [--use-saved | --preset "Name"] [--exit-when-done] [video files or folders...]
    // Dropping files onto the exe passes them as arguments.
    //   --use-saved       start straight away with the last run's settings
    //   --preset "Name"   start straight away with a saved preset
    //   --exit-when-done  with either of the above, close the window when finished (for scripts)
    //   --diagnostics     check the GPU encoders, write diagnostics.txt to the settings folder and open it
    //                     (with a video: also test-encode its first seconds with every encoder)
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);

        bool useSaved = false, exitWhenDone = false;
        string? preset = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--use-saved", StringComparison.OrdinalIgnoreCase)) useSaved = true;
            else if (a.Equals("--exit-when-done", StringComparison.OrdinalIgnoreCase)) exitWhenDone = true;
            else if (a.Equals("--preset", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) preset = args[++i];
            else if (!a.StartsWith("--", StringComparison.Ordinal)) paths.Add(a);
        }
        if (args.Any(a => a.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase)))
        {
            WriteDiagnostics(paths.FirstOrDefault());
            return;
        }
        Application.Run(new MainForm(paths, useSaved, preset, exitWhenDone));
    }

    // --diagnostics [video]: checks the encoders afresh (and test-encodes the video, if given),
    // writes diagnostics.txt to the settings folder and opens it, without the main window
    static void WriteDiagnostics(string? video)
    {
        Task.Run(async () =>
        {
            List<Worker>? workers = null;
            string? trial = null;
            if (Ffmpeg.Locate())
            {
                try { workers = await EncoderCaps.DetectAsync(null, CancellationToken.None, force: true); }
                catch (Exception e) { EncoderCaps.LastError = e.ToString(); }
                if (workers != null && video != null)
                {
                    var lines = new System.Text.StringBuilder($"Video: {video}\n");
                    await JobRunner.TrialAsync(video, AppSettings.Load(), workers, l => { lock (lines) lines.AppendLine(l); }, CancellationToken.None);
                    trial = lines.ToString();
                }
            }
            var report = await Diagnostics.BuildAsync(workers, AppSettings.Load(), trial, CancellationToken.None);
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(Diagnostics.ReportPath, report.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }).GetAwaiter().GetResult();
        if (Environment.GetEnvironmentVariable("VIDEO_TIMECODE_NO_OPEN") == null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Diagnostics.ReportPath) { UseShellExecute = true });
    }

    // Saves the details for bug reports and keeps the app running
    static void ReportCrash(Exception ex)
    {
        var path = Path.Combine(AppSettings.Dir, "error.log");
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(path, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Video Timecode {typeof(Program).Assembly.GetName().Version}\r\n{ex}\r\n\r\n");
        }
        catch { }
        MessageBox.Show($"Something went wrong: {ex.Message}\n\nThe details were saved to:\n{path}",
            "Video Timecode", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
