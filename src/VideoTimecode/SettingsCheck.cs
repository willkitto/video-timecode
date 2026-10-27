namespace VideoTimecode;

// The setting a problem is about, so the window can highlight it
public enum CheckTarget { StartTime, Marks, TimeZone }

// Something about the settings that probably isn't what the user wants for these videos.
// Short goes in the file list; Message is the full sentence.
sealed record CheckIssue(string? Path, CheckTarget Target, string Short, string Message);

// Looks at the time settings against what each video actually contains
static class SettingsCheck
{
    public static List<CheckIssue> Check(AppSettings s, IReadOnlyList<VideoInfo> files)
    {
        var issues = new List<CheckIssue>();
        if (s.StartMode == StartTimeMode.Manual)
        {
            if (files.Count > 1)
                issues.Add(new CheckIssue(null, CheckTarget.StartTime, "",
                    $"Start time is set manually, so all {files.Count} videos will start at the same time."));
            return issues;   // nothing from the files is used
        }

        TimeZoneInfo? zone = null;
        if (!string.IsNullOrEmpty(s.TimeZoneId))
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId); } catch { }

        foreach (var v in files)
        {
            var name = Path.GetFileName(v.Path);
            if (!v.HasDate) continue;   // can't be read at all; the run reports that

            if (v.DateFromFileTime)
                issues.Add(new CheckIssue(v.Path, CheckTarget.StartTime, "no date inside, so an estimate: check it",
                    $"{name} has no recording date inside, so its modified time is used as the end of recording. " +
                    "That is only an estimate (copying or editing the file can change it): check it, or set the start time manually."));

            var start = v.Start;
            if (s.StartMode == StartTimeMode.Shifted) start = start.AddSeconds(s.StartShiftSeconds);
            if (!v.FixedOffset) start = start.ToLocalTime();   // as the file list shows it
            if (start.Year < 2001 || start > DateTimeOffset.Now.AddDays(1))
                issues.Add(new CheckIssue(v.Path, CheckTarget.StartTime, "date looks wrong (clock not set?)",
                    $"{name} says it was recorded {start:yyyy-MM-dd HH:mm}: the camera's clock was probably not set. " +
                    "Use a shift or set the start time manually."));

            if (s.CreationMarks != CreationMarks.Auto && v.MarksKnown && !v.DateFromFileTime
                && (s.CreationMarks == CreationMarks.End) != v.DateMarksEnd)
                issues.Add(new CheckIssue(v.Path, CheckTarget.Marks, $"its date is the {(v.DateMarksEnd ? "end, not the start" : "start, not the end")}",
                    $"{name}: {v.MarksReason}, but Embedded date is set to {(s.CreationMarks == CreationMarks.End ? "End" : "Start")} of recording. " +
                    "Automatic would handle it."));

            if (zone != null && v.FixedOffset)
            {
                var chosen = zone.GetUtcOffset(v.Start);
                if (chosen != v.Start.Offset)
                    issues.Add(new CheckIssue(v.Path, CheckTarget.TimeZone, $"recorded at UTC{Offset(v.Start.Offset)}, not the chosen zone",
                        $"{name} was recorded at UTC{Offset(v.Start.Offset)}, but the chosen time zone is UTC{Offset(chosen)} then, " +
                        "so its times will be converted. Choose Automatic to keep the camera's own time."));
            }
        }
        return issues;
    }

    static string Offset(TimeSpan t) => (t < TimeSpan.Zero ? "-" : "+") + t.Duration().ToString(@"hh\:mm");
}
