using System.Text.RegularExpressions;

namespace VideoTimecode;

// Named presets: one settings file each in <settings dir>\presets, so they can be
// copied between PCs. "Last run" (settings.json) and "Defaults" are always available.
static class Presets
{
    public const string LastRun = "Last run";
    public const string Defaults = "Defaults";

    public static string Dir => Path.Combine(AppSettings.Dir, "presets");

    // Names also become file names and appear in .bat files, so keep to characters safe in both
    static readonly Regex ValidName = new(@"^[A-Za-z0-9][A-Za-z0-9 _.,'()+#@-]{0,59}$");

    public static string? Problem(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Enter a name.";
        if (name.Equals(LastRun, StringComparison.OrdinalIgnoreCase) || name.Equals(Defaults, StringComparison.OrdinalIgnoreCase))
            return $"\"{name}\" is reserved.";
        if (!ValidName.IsMatch(name))
            return "Use up to 60 letters, numbers, spaces and  _ . , ' ( ) + # @ -  (starting with a letter or number).";
        return null;
    }

    public static List<string> Names()
    {
        if (!Directory.Exists(Dir)) return new();
        return Directory.GetFiles(Dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool Exists(string name) => File.Exists(PathOf(name));

    // Finds a preset by name, ignoring case; includes "Last run" and "Defaults"
    public static AppSettings? Load(string name)
    {
        if (name.Equals(LastRun, StringComparison.OrdinalIgnoreCase)) return AppSettings.Load();
        if (name.Equals(Defaults, StringComparison.OrdinalIgnoreCase)) return new AppSettings();
        var match = Names().FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : AppSettings.LoadFile(PathOf(match));
    }

    public static void Save(string name, AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        s.SaveFile(PathOf(name.Trim()));
    }

    public static void Delete(string name) => File.Delete(PathOf(name));

    public static void Rename(string from, string to) => File.Move(PathOf(from), PathOf(to.Trim()));

    static string PathOf(string name) => Path.Combine(Dir, name + ".json");
}
