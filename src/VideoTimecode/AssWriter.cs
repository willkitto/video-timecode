using System.Globalization;
using System.Text;

namespace VideoTimecode;

// Writes the overlays as an .ass subtitle file. Each position becomes one event per
// change of text; consecutive frames with identical text are merged into one cue.
static class AssWriter
{
    public static void Write(string path, TemplateContext c)
    {
        var s = c.Settings;
        var v = c.Info;
        int w = Math.Max(2, v.Width), h = Math.Max(2, v.Height);
        int margin = (int)Math.Round(h / 30.0);
        bool box = s.Edge == EdgeStyle.Box;
        int edgeAlpha = box ? (int)Math.Round(255 * (1 - Math.Clamp(s.BoxOpacity, 0, 100) / 100.0)) : 0;
        string font = s.FontName.Replace(",", " ");

        var sb = new StringBuilder();
        sb.Append("[Script Info]\r\nScriptType: v4.00+\r\n");
        sb.Append($"PlayResX: {w}\r\nPlayResY: {h}\r\n");
        sb.Append("ScaledBorderAndShadow: yes\r\nWrapStyle: 2\r\nYCbCr Matrix: None\r\n\r\n");
        sb.Append("[V4+ Styles]\r\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n");
        sb.Append($"Style: Default,{font},{h / 20},&H00FFFFFF,&H00FFFFFF,{ColorUtil.ToAssStyle(ColorUtil.Parse(s.EdgeColor), edgeAlpha)},&H00000000,{(s.Bold ? -1 : 0)},0,0,0,100,100,0,0,{(box ? 3 : 1)},2,0,2,{margin},{margin},{margin},1\r\n\r\n");
        sb.Append("[Events]\r\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");

        var times = v.FrameTimes;
        int n = Math.Max(1, times.Count);
        double first = times.Count > 0 ? times[0] : 0;
        long endCs = (long)Math.Floor(c.ClipLength * 100);

        var groups = s.Overlays
            .Where(o => o.Enabled && !string.IsNullOrWhiteSpace(o.Text))
            .GroupBy(o => o.Position);

        foreach (var g in groups)
        {
            var rows = g.Select(o =>
            {
                int px = Math.Max(1, (int)Math.Round(h * o.Size / 100));
                int bord = box ? Math.Max(1, px / 5) : Math.Max(1, (int)Math.Round(px / 14.0));
                var font = o.FontName == null ? "" : "\\fn" + o.FontName.Replace("\\", "").Replace("{", "").Replace("}", "");
                var bold = o.Bold is { } b ? $"\\b{(b ? 1 : 0)}" : "";
                var prefix = $"{{{font}{bold}\\fs{px}\\bord{bord}\\c{ColorUtil.ToAssInline(ColorUtil.Parse(o.Color))}}}";
                return (Template: new Template(o.Text), Prefix: prefix);
            }).ToList();
            string an = $"{{\\an{(int)g.Key}}}";
            string Line(int i, double rel) =>
                an + string.Join("\\N", rows.Select(r => r.Prefix + Escape(r.Template.Render(c, i, rel))));

            if (!rows.Any(r => r.Template.IsDynamic))
            {
                Event(sb, 0, endCs, Line(0, 0));
                continue;
            }

            string? cur = null;
            long curStart = 0;
            for (int i = 0; i < n; i++)
            {
                double rel = times.Count > 0 ? times[i] - first : 0;
                long cs = (long)Math.Floor(rel * 100);   // round down so each cue starts at or before its frame
                var text = Line(i, rel);
                if (text == cur) continue;
                if (cur != null) Event(sb, curStart, cs, cur);
                cur = text;
                curStart = cs;
            }
            if (cur != null) Event(sb, curStart, endCs, cur);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    static void Event(StringBuilder sb, long startCs, long endCs, string text)
    {
        if (endCs <= startCs) return;
        sb.Append("Dialogue: 0,").Append(Time(startCs)).Append(',').Append(Time(endCs))
          .Append(",Default,,0,0,0,,").Append(text).Append("\r\n");
    }

    static string Time(long cs) =>
        string.Create(CultureInfo.InvariantCulture, $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");

    // Braces would start an ASS override block, so swap them for parentheses
    static string Escape(string s) =>
        s.Replace("\r", "").Replace("\n", "\\N").Replace('{', '(').Replace('}', ')');
}
