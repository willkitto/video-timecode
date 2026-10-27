using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace VideoTimecode;

// Draws the overlays on a sample 16:9 frame, using the same layout and font sizing as the .ass file
sealed class PreviewPanel : Control
{
    public Func<AppSettings>? Settings;

    public PreviewPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    static TemplateContext SampleContext(AppSettings s)
    {
        var info = new VideoInfo
        {
            Path = "C0039.MP4",
            Width = 3840,
            Height = 2160,
            Codec = "h264",
            Fps = 29.97,
            Duration = 30.03,
            Camera = "Sony FDR-AX43",
            Gps = "37.77490, -122.41940",
            Timecode = "00:30:09;24",
            Start = DateTimeOffset.Now.AddSeconds(-4.07),
            HasDate = true,
            FixedOffset = true,
        };
        return new TemplateContext(info, s, string.IsNullOrEmpty(s.TimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(BackColor);

        // Largest 16:9 frame that fits
        float fw = Width - 2, fh = fw * 9 / 16;
        if (fh > Height - 2) { fh = Height - 2; fw = fh * 16 / 9; }
        var frame = new RectangleF((Width - fw) / 2, (Height - fh) / 2, fw, fh);
        using (var bg = new LinearGradientBrush(frame, Color.FromArgb(35, 40, 50), Color.FromArgb(215, 205, 180), 0f))
            g.FillRectangle(bg, frame);
        g.DrawRectangle(SystemPens.ControlDark, frame.X, frame.Y, frame.Width, frame.Height);

        var s = Settings?.Invoke();
        if (s == null || frame.Height < 10) return;
        TemplateContext ctx;
        try { ctx = SampleContext(s); }
        catch { return; }
        int alpha = (int)(255 * Math.Clamp(s.BoxOpacity, 0, 100) / 100.0);

        foreach (var grp in s.Overlays.Where(o => o.Enabled && !string.IsNullOrWhiteSpace(o.Text)).GroupBy(o => o.Position))
        {
            var align = s.Alignments.GetValueOrDefault(grp.Key);
            var rows = grp.Select(o =>
            {
                double px = frame.Height * o.Size / 100;
                var edge = o.Edge ?? s.Edge;
                return (O: o, Text: new Template(o.Text).Render(ctx, 122, 4.07).Replace("\r", ""), Px: px,
                        Font: o.FontName ?? s.FontName, Bold: o.Bold ?? s.Bold, Edge: edge,
                        EdgeColor: ColorUtil.Parse(o.EdgeColor ?? s.EdgeColor), Pad: edge == EdgeStyle.Box ? Math.Max(1, px / 5) : 0);
            }).ToList();
            var sizes = rows.Select(r =>
            {
                var lines = r.Text.Split('\n');
                return new RowSize(r.Px, lines.Length, r.Pad, lines.Max(l => FontMetrics.Width(l, r.Font, r.Bold, r.Px)));
            }).ToList();
            var places = OverlayLayout.Place(grp.Key, align, sizes, frame.Width, frame.Height);

            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                var p = places[k];
                var lines = r.Text.Split('\n');
                for (int li = 0; li < lines.Length; li++)
                    DrawLine(g, lines[li], r.Font, r.Bold, r.Px, ColorUtil.Parse(r.O.Color), r.Edge, r.EdgeColor, alpha, r.Pad,
                        frame.Left + p.X, frame.Top + p.Y + li * r.Px, p.An);
            }
        }
    }

    // One line of text whose line box starts at top; x is its left, centre or right per the anchor
    static void DrawLine(Graphics g, string text, string font, bool bold, double px, Color color,
        EdgeStyle edge, Color edgeColor, int alpha, double pad, double x, double top, int an)
    {
        if (text.Length == 0 || px < 1) return;
        var m = FontMetrics.Get(font, bold);
        float em = (float)(px / m.LineHeight);
        using var f = FontMetrics.MakeFont(font, bold, em);
        var family = f.FontFamily;
        double width = FontMetrics.Width(text, font, bold, px);
        double left = an switch { 7 => x, 8 => x - width / 2, _ => x - width };

        // Put the baseline where the renderer would: the font's Windows ascent below the line top
        double baseline = top + em * m.Ascent;
        double cellAscent = em * family.GetCellAscent(f.Style) / (double)family.GetEmHeight(f.Style);
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)f.Style, em, new PointF((float)left, (float)(baseline - cellAscent)), StringFormat.GenericTypographic);

        float bord = (float)Math.Max(0.5, px / 14);
        switch (edge)
        {
            case EdgeStyle.Box:
                using (var br = new SolidBrush(Color.FromArgb(alpha, edgeColor)))
                    g.FillRectangle(br, (float)(left - pad), (float)(top - pad), (float)(width + 2 * pad), (float)(px + 2 * pad));
                break;
            case EdgeStyle.Outline:
                using (var pen = new Pen(edgeColor, bord * 2) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
                break;
            case EdgeStyle.Shadow:
                using (var shadow = (GraphicsPath)path.Clone())
                using (var mx = new Matrix())
                using (var br = new SolidBrush(Color.FromArgb(alpha, edgeColor)))
                {
                    mx.Translate(bord, bord);
                    shadow.Transform(mx);
                    g.FillPath(br, shadow);
                }
                break;
        }
        using var fill = new SolidBrush(color);
        g.FillPath(fill, path);
    }
}
