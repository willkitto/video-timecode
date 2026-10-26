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
        bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal));
        Application.Run(new MainForm(paths, Flag("--use-saved"), Flag("--exit-when-done")));
    }
}
