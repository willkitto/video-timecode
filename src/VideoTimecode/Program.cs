namespace VideoTimecode;

static class Program
{
    // Usage: VideoTimecode.exe [--use-saved | --preset "Name"] [--exit-when-done] [video files or folders...]
    // Dropping files onto the exe passes them as arguments.
    //   --use-saved       start straight away with the last run's settings
    //   --preset "Name"   start straight away with a saved preset
    //   --exit-when-done  with either of the above, close the window when finished (for scripts)
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
        Application.Run(new MainForm(paths, useSaved, preset, exitWhenDone));
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
