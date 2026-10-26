namespace VideoTimecode;

static class Program
{
    // Usage: VideoTimecode.exe [--use-saved [--exit-when-done]] [video files or folders...]
    // Dropping files onto the exe passes them as arguments.
    //   --use-saved       start straight away with the last saved settings
    //   --exit-when-done  with --use-saved, close the window when finished (for scripts)
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal));
        Application.Run(new MainForm(paths, Flag("--use-saved"), Flag("--exit-when-done")));
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
