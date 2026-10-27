using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace VideoTimecode;

// Draws the overlays on a sample 16:9 frame, laid out the way the .ass file lays them out
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
            FixedOffset = true,
        };
        return new TemplateContext(info, s, TimeZoneInfo.Local);
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
        var ctx = SampleContext(s);
        bool box = s.Edge == EdgeStyle.Box;
        var edge = ColorUtil.Parse(s.EdgeColor);
        var edgeColor = box ? Color.FromArgb((int)(255 * Math.Clamp(s.BoxOpacity, 0, 100) / 100.0), edge) : edge;
        float margin = frame.Height / 30;

        // Each overlay can have its own font; cache the families used
        var families = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        FontFamily FamilyOf(string name) => families.TryGetValue(name, out var f) ? f : families[name] = TryFamily(name);

        try
        {
            foreach (var grp in s.Overlays.Where(o => o.Enabled && !string.IsNullOrWhiteSpace(o.Text)).GroupBy(o => o.Position))
                DrawGroup(g, grp, frame, margin, ctx, box, edgeColor, s, FamilyOf);
        }
        finally
        {
            foreach (var f in families.Values) f.Dispose();
        }
    }

    static void DrawGroup(Graphics g, IGrouping<Anchor9, Overlay> grp, RectangleF frame, float margin, TemplateContext ctx,
        bool box, Color edgeColor, AppSettings s, Func<string, FontFamily> familyOf)
    {
        int an = (int)grp.Key;
        int col = (an - 1) % 3, row = (an - 1) / 3;   // col: 0 left 1 center 2 right; row: 0 bottom 1 middle 2 top
        var lines = grp.SelectMany(o =>
        {
            float px = (float)(frame.Height * o.Size / 100);
            var family = familyOf(o.FontName ?? s.FontName);
            bool bold = o.Bold ?? s.Bold;
            var style = bold && family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold
                      : family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            return new Template(o.Text).Render(ctx, 122, 4.07).Replace("\r", "").Split('\n')
                .Select(t => (Text: t, Px: px, Color: ColorUtil.Parse(o.Color), Family: family, Style: style));
        }).ToList();

        float total = lines.Sum(l => l.Px);
        float y = row switch
        {
            2 => frame.Top + margin,
            1 => frame.Top + (frame.Height - total) / 2,
            _ => frame.Bottom - margin - total,
        };

        foreach (var (text, px, color, family, style) in lines)
        {
            if (text.Length > 0 && px >= 1)
            {
                // ASS font size is the full line height; GDI+ wants the em size
                float em = px * family.GetEmHeight(style) / (family.GetCellAscent(style) + family.GetCellDescent(style));
                using var path = new GraphicsPath();
                path.AddString(text, family, (int)style, em, PointF.Empty, StringFormat.GenericTypographic);
                var b = path.GetBounds();
                float x = col switch
                {
                    0 => frame.Left + margin,
                    1 => frame.Left + (frame.Width - b.Width) / 2,
                    _ => frame.Right - margin - b.Width,
                };
                using var m = new Matrix();
                m.Translate(x - b.X, y + (px - b.Height) / 2 - b.Y);
                path.Transform(m);

                float bord = box ? Math.Max(1, px / 5) : Math.Max(0.5f, px / 14);
                if (box)
                {
                    var r = path.GetBounds();
                    r.Inflate(bord, bord);
                    using var br = new SolidBrush(edgeColor);
                    g.FillRectangle(br, r);
                }
                else
                {
                    using var pen = new Pen(edgeColor, bord * 2) { LineJoin = LineJoin.Round };
                    g.DrawPath(pen, path);
                }
                using var fill = new SolidBrush(color);
                g.FillPath(fill, path);
            }
            y += px;
        }
    }

    static FontFamily TryFamily(string name)
    {
        try { return new FontFamily(name); }
        catch { return new FontFamily(GenericFontFamilies.SansSerif); }
    }
}
