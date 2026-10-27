using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace VideoTimecode;

// Draws the overlays on a sample 16:9 frame, using the same layout and font sizing as the .ass file
sealed class PreviewPanel : Control
{
    public Func<AppSettings>? Settings;

    // The area highlighted while a picture is dragged over the preview
    Anchor9? dropTarget;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Anchor9? DropTarget
    {
        get => dropTarget;
        set { if (dropTarget != value) { dropTarget = value; Invalidate(); } }
    }

    public PreviewPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    // Largest 16:9 frame that fits
    RectangleF Frame()
    {
        float fw = Width - 2, fh = fw * 9 / 16;
        if (fh > Height - 2) { fh = Height - 2; fw = fh * 16 / 9; }
        return new RectangleF((Width - fw) / 2, (Height - fh) / 2, fw, fh);
    }

    // Which of the nine positions a point in the preview falls in
    public Anchor9 AnchorAt(Point p)
    {
        var f = Frame();
        int col = Math.Clamp((int)(3 * (p.X - f.Left) / Math.Max(1, f.Width)), 0, 2);
        int band = 2 - Math.Clamp((int)(3 * (p.Y - f.Top) / Math.Max(1, f.Height)), 0, 2);   // 2 = top
        return (Anchor9)(band * 3 + col + 1);
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

        var frame = Frame();
        using (var bg = new LinearGradientBrush(frame, Color.FromArgb(35, 40, 50), Color.FromArgb(215, 205, 180), 0f))
            g.FillRectangle(bg, frame);
        g.DrawRectangle(SystemPens.ControlDark, frame.X, frame.Y, frame.Width, frame.Height);
        if (dropTarget is { } t)
        {
            int col = ((int)t - 1) % 3, band = ((int)t - 1) / 3;
            var cell = new RectangleF(frame.Left + col * frame.Width / 3, frame.Top + (2 - band) * frame.Height / 3, frame.Width / 3, frame.Height / 3);
            using var hl = new SolidBrush(Color.FromArgb(70, 80, 160, 255));
            g.FillRectangle(hl, cell);
            using var pen = new Pen(Color.FromArgb(200, 120, 190, 255), 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, cell.X, cell.Y, cell.Width, cell.Height);
        }

        var s = Settings?.Invoke();
        if (s == null || frame.Height < 10) return;
        TemplateContext ctx;
        try { ctx = SampleContext(s); }
        catch { return; }
        int alpha = (int)(255 * Math.Clamp(s.BoxOpacity, 0, 100) / 100.0);

        bool Shown(Overlay o) => o.Enabled && (o.IsImage ? OverlayImages.SizeOf(o.ImagePath!) != null : !string.IsNullOrWhiteSpace(o.Text));
        bool imagesBurned = s.Mode == OutputMode.Burn;

        foreach (var grp in s.Overlays.Where(Shown).GroupBy(o => o.Position))
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
                if (OverlayImages.Scaled(r.O, frame.Height) is { } img) return new RowSize(img.H, 1, 0, img.W);
                var lines = r.Text.Split('\n');
                return new RowSize(r.Px, lines.Length, r.Pad, lines.Max(l => FontMetrics.Width(l, r.Font, r.Bold, r.Px)));
            }).ToList();
            var places = OverlayLayout.Place(grp.Key, align, sizes, frame.Width, frame.Height);

            for (int k = 0; k < rows.Count; k++)
            {
                var r = rows[k];
                var p = places[k];
                if (r.O.IsImage)
                {
                    var size = sizes[k];
                    double left = p.An switch { 7 => p.X, 8 => p.X - size.Width / 2, _ => p.X - size.Width };
                    DrawImage(g, r.O.ImagePath!, new RectangleF((float)(frame.Left + left), (float)(frame.Top + p.Y), (float)size.Width, (float)size.Px), imagesBurned);
                    continue;
                }
                var lines = r.Text.Split('\n');
                for (int li = 0; li < lines.Length; li++)
                    DrawLine(g, lines[li], r.Font, r.Bold, r.Px, ColorUtil.Parse(r.O.Color), r.Edge, r.EdgeColor, alpha, r.Pad,
                        frame.Left + p.X, frame.Top + p.Y + li * r.Px, p.An);
            }
        }
    }

    readonly Dictionary<string, (DateTime Stamp, Bitmap Image)> imageCache = new(StringComparer.OrdinalIgnoreCase);

    // A picture overlay; faded with a dashed border when the output type can't include it
    void DrawImage(Graphics g, string path, RectangleF r, bool burned)
    {
        Bitmap img;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (!imageCache.TryGetValue(path, out var cached) || cached.Stamp != stamp)
            {
                if (imageCache.TryGetValue(path, out var old)) old.Image.Dispose();
                imageCache[path] = cached = (stamp, OverlayImages.Load(path));
            }
            img = cached.Image;
        }
        catch { return; }

        if (burned)
        {
            g.DrawImage(img, r);
            return;
        }
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
        g.DrawImage(img, Rectangle.Round(r), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
        using var pen = new Pen(Color.White) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var c in imageCache.Values) c.Image.Dispose();
        base.Dispose(disposing);
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
