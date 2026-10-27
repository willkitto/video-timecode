using System.Globalization;
using System.Text;

namespace VideoTimecode;

// Writes the overlays as an .ass subtitle file. Each overlay row is its own stream of
// events, placed explicitly (\pos) so rows can differ in edge style (outline, box, shadow,
// none) and line up as chosen; consecutive frames with identical text share one cue.
static class AssWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // One overlay row, resolved against the Style defaults
    sealed record Row(Overlay O, Template Template, double Px, string Font, bool Bold, EdgeStyle Edge, Color EdgeColor, double Pad)
    {
        public static Row From(Overlay o, AppSettings s, double frameHeight)
        {
            double px = Math.Max(1, Math.Round(frameHeight * o.Size / 100));
            var edge = o.Edge ?? s.Edge;
            return new Row(o, new Template(o.Text), px, o.FontName ?? s.FontName, o.Bold ?? s.Bold, edge,
                ColorUtil.Parse(o.EdgeColor ?? s.EdgeColor), edge == EdgeStyle.Box ? Math.Max(1, Math.Round(px / 5)) : 0);
        }

        public double Outline => Math.Max(1, Math.Round(Px / 14));
    }

    public static void Write(string path, TemplateContext c)
    {
        var s = c.Settings;
        var v = c.Info;
        int w = Math.Max(2, v.Width), h = Math.Max(2, v.Height);
        int alpha = TransparencyByte(s.BoxOpacity);
        string font = s.FontName.Replace(",", " ");
        int bold = s.Bold ? -1 : 0;

        var sb = new StringBuilder();
        sb.Append("[Script Info]\r\nScriptType: v4.00+\r\n");
        sb.Append($"PlayResX: {w}\r\nPlayResY: {h}\r\n");
        sb.Append("ScaledBorderAndShadow: yes\r\nWrapStyle: 2\r\nYCbCr Matrix: None\r\n\r\n");
        sb.Append("[V4+ Styles]\r\n");
        sb.Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n");
        // Box behind text needs its own style: the border style can't change within a style
        sb.Append($"Style: Default,{font},{h / 20},&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,{bold},0,0,0,100,100,0,0,1,0,0,7,0,0,0,1\r\n");
        sb.Append($"Style: Box,{font},{h / 20},&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,{bold},0,0,0,100,100,0,0,3,0,0,7,0,0,0,1\r\n\r\n");
        sb.Append("[Events]\r\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");

        var times = v.FrameTimes;
        int n = Math.Max(1, times.Count);
        double first = times.Count > 0 ? times[0] : 0;
        double Rel(int i) => times.Count > 0 ? times[i] - first : 0;
        long endCs = (long)Math.Floor(c.ClipLength * 100);
        var samples = SampleFrames(n);

        foreach (var g in s.Overlays.Where(o => o.Enabled && !string.IsNullOrWhiteSpace(o.Text)).GroupBy(o => o.Position))
        {
            var align = s.Alignments.GetValueOrDefault(g.Key);
            var rows = g.Select(o => Row.From(o, s, h)).ToList();

            // Size each row from a spread of frames, so the layout holds for the whole clip
            var sizes = rows.Select(r =>
            {
                var texts = samples.Select(i => r.Template.Render(c, i, Rel(i))).ToList();
                int lines = texts.Max(t => t.Count(ch => ch == '\n') + 1);
                double width = align == RowAlign.Auto ? 0
                    : texts.Max(t => t.Split('\n').Max(l => FontMetrics.Width(l, r.Font, r.Bold, r.Px)));
                return new RowSize(r.Px, lines, r.Pad, width);
            }).ToList();
            var places = OverlayLayout.Place(g.Key, align, sizes, w, h);

            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                var p = places[k];
                string tags = Tags(r, p, alpha);
                string style = r.Edge == EdgeStyle.Box ? "Box" : "Default";

                if (!r.Template.IsDynamic)
                {
                    Event(sb, 0, endCs, style, tags + Escape(r.Template.Render(c, 0, 0)));
                    continue;
                }
                string? cur = null;
                long curStart = 0;
                for (int i = 0; i < n; i++)
                {
                    long cs = (long)Math.Floor(Rel(i) * 100);   // round down so each cue starts at or before its frame
                    var text = r.Template.Render(c, i, Rel(i));
                    if (text == cur) continue;
                    if (cur != null) Event(sb, curStart, cs, style, tags + Escape(cur));
                    cur = text;
                    curStart = cs;
                }
                if (cur != null) Event(sb, curStart, endCs, style, tags + Escape(cur));
            }
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // Override tags for one row: placement, font, size, colour and edge
    static string Tags(Row r, RowPlacement p, int alpha)
    {
        var t = new StringBuilder("{");
        t.Append(Inv, $"\\an{p.An}\\pos({p.X:0},{p.Y:0})");
        if (r.O.FontName != null) t.Append("\\fn").Append(r.O.FontName.Replace("\\", "").Replace("{", "").Replace("}", ""));
        if (r.O.Bold is { } b) t.Append(b ? "\\b1" : "\\b0");
        t.Append(Inv, $"\\fs{r.Px:0}\\c{ColorUtil.ToAssInline(ColorUtil.Parse(r.O.Color))}");
        var ec = ColorUtil.ToAssInline(r.EdgeColor);
        t.Append(r.Edge switch
        {
            EdgeStyle.Outline => $"\\bord{r.Outline:0}\\shad0\\3c{ec}",
            EdgeStyle.Shadow => $"\\bord0\\shad{r.Outline:0}\\4c{ec}\\4a&H{alpha:X2}&",
            EdgeStyle.Box => $"\\bord{r.Pad:0}\\shad0\\3c{ec}\\3a&H{alpha:X2}&",
            _ => "\\bord0\\shad0",
        });
        return t.Append('}').ToString();
    }

    // ASS alpha: 00 is opaque, FF is invisible
    static int TransparencyByte(int opacityPercent) => (int)Math.Round(255 * (1 - Math.Clamp(opacityPercent, 0, 100) / 100.0));

    static List<int> SampleFrames(int n)
    {
        int step = Math.Max(1, n / 60);
        var list = Enumerable.Range(0, (n + step - 1) / step).Select(i => i * step).ToList();
        if (list[^1] != n - 1) list.Add(n - 1);
        return list;
    }

    static void Event(StringBuilder sb, long startCs, long endCs, string style, string text)
    {
        if (endCs <= startCs) return;
        sb.Append("Dialogue: 0,").Append(Time(startCs)).Append(',').Append(Time(endCs))
          .Append(',').Append(style).Append(",,0,0,0,,").Append(text).Append("\r\n");
    }

    static string Time(long cs) =>
        string.Create(Inv, $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");

    // Braces would start an ASS override block, so swap them for parentheses
    static string Escape(string s) =>
        s.Replace("\r", "").Replace("\n", "\\N").Replace('{', '(').Replace('}', ')');
}
