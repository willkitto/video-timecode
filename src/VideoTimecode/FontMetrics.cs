using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VideoTimecode;

// Font measurements the way subtitle renderers (libass) see them. They size text so the
// font's Windows ascent + descent equals the chosen size; a few fonts (e.g. Cambria Math,
// at 5.6x its letter size) set those far larger than their letters, so the text comes out tiny.
static class FontMetrics
{
    // Ascent and descent as fractions of the font's em size
    public readonly record struct Metrics(double Ascent, double Descent)
    {
        public double LineHeight => Ascent + Descent;
    }

    // Fonts whose letters come out at well under the chosen size
    public const double TallLineHeight = 1.6;

    static readonly ConcurrentDictionary<(string Family, bool Bold), Metrics> Cache = new();

    public static Metrics Get(string family, bool bold) => Cache.GetOrAdd((family, bold), k => Measure(k.Family, k.Bold));

    static Metrics Measure(string family, bool bold)
    {
        using var font = MakeFont(family, bold, 100);
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), hfont = font.ToHfont();
        try
        {
            var old = SelectObject(dc, hfont);
            bool ok = GetTextMetricsW(dc, out var tm);
            SelectObject(dc, old);
            if (ok && tm.tmAscent + tm.tmDescent > 0) return new Metrics(tm.tmAscent / 100.0, tm.tmDescent / 100.0);
        }
        finally
        {
            DeleteObject(hfont);
            DeleteDC(dc);
        }
        return new Metrics(0.9, 0.25);
    }

    // Width in pixels of one line of text whose line height is px, as the renderer would size it
    public static double Width(string text, string family, bool bold, double px)
    {
        if (text.Length == 0 || px <= 0) return 0;
        double em = px / Get(family, bold).LineHeight;
        using var font = MakeFont(family, bold, (float)Math.Max(0.1, em));
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return g.MeasureString(text, font, PointF.Empty, fmt).Width;
    }

    public static Font MakeFont(string family, bool bold, float pixels)
    {
        foreach (var style in bold ? new[] { FontStyle.Bold, FontStyle.Regular } : new[] { FontStyle.Regular, FontStyle.Bold })
        {
            try { return new Font(family, pixels, style, GraphicsUnit.Pixel); }
            catch (ArgumentException) { }
        }
        return new Font(FontFamily.GenericSansSerif, pixels, GraphicsUnit.Pixel);
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW tm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
                   tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }
}
