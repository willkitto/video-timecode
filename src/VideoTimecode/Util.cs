using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoTimecode;

static class ColorUtil
{
    public static Color Parse(string? s)
    {
        s = (s ?? "").Trim();
        var hex = s.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        var named = Color.FromName(s);
        return named.IsKnownColor ? named : Color.White;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ASS colours are blue-green-red; style colours carry alpha (00 = opaque)
    public static string ToAssStyle(Color c, int alpha = 0) => $"&H{alpha:X2}{c.B:X2}{c.G:X2}{c.R:X2}";
    public static string ToAssInline(Color c) => $"&H{c.B:X2}{c.G:X2}{c.R:X2}&";

    public static Color Contrasting(Color c) =>
        (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 128 ? Color.Black : Color.White;
}

// Pixel formats boiled down to what matters for matching quality: chroma subsampling and bit depth
static class Pix
{
    public static readonly string[] Classes = { "420-8", "420-10", "422-8", "422-10", "444-8", "444-10" };

    public static string ClassOf(string pixFmt)
    {
        var pf = pixFmt.Trim().ToLowerInvariant();
        var m = Regex.Match(pf, @"^yuva?j?(420|422|444)p(\d+)?");
        if (m.Success)
        {
            int depth = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 8;
            return $"{m.Groups[1].Value}-{(depth > 8 ? 10 : 8)}";
        }
        return pf switch
        {
            "nv12" or "nv21" or "gray" => "420-8",
            "p010le" or "p010be" or "p012le" or "p016le" or "gray10le" or "gray12le" => "420-10",
            "nv16" => "422-8",
            "p210le" or "p212le" or "p216le" => "422-10",
            "nv24" or "nv42" => "444-8",
            "p410le" or "p412le" or "p416le" => "444-10",
            _ => "",
        };
    }

    // Software pixel format to request from CPU encoders for a class
    public static string SoftwareFormat(string cls) => cls switch
    {
        "420-10" => "yuv420p10le",
        "422-8" => "yuv422p",
        "422-10" => "yuv422p10le",
        "444-8" => "yuv444p",
        "444-10" => "yuv444p10le",
        _ => "yuv420p",
    };

    public static string Describe(string cls) => cls switch
    {
        "420-8" => "8-bit 4:2:0",
        "420-10" => "10-bit 4:2:0",
        "422-8" => "8-bit 4:2:2",
        "422-10" => "10-bit 4:2:2",
        "444-8" => "8-bit 4:4:4",
        "444-10" => "10-bit 4:4:4",
        _ => "unknown format",
    };
}

// SMPTE timecode with drop-frame support (29.97 / 59.94)
static class Smpte
{
    public static bool TryParse(string? s, double fps, out long frame, out int nominal, out bool drop)
    {
        frame = 0; nominal = Math.Max(1, (int)Math.Round(fps > 0 ? fps : 30)); drop = false;
        if (s == null) return false;
        var m = Regex.Match(s.Trim(), @"^(\d+):(\d+):(\d+)([:;.,])(\d+)$");
        if (!m.Success) return false;
        long hh = long.Parse(m.Groups[1].Value), mm = long.Parse(m.Groups[2].Value),
             ss = long.Parse(m.Groups[3].Value), ff = long.Parse(m.Groups[5].Value);
        drop = m.Groups[4].Value is ";" or "," && nominal % 30 == 0;
        frame = (hh * 3600 + mm * 60 + ss) * nominal + ff;
        if (drop)
        {
            long minutes = hh * 60 + mm;
            frame -= DropPerMinute(nominal) * (minutes - minutes / 10);
        }
        return true;
    }

    public static string Format(long frame, int nominal, bool drop)
    {
        if (drop)
        {
            int d = DropPerMinute(nominal);
            long per10 = nominal * 600L - d * 9L;
            long perMin = nominal * 60L - d;
            long tens = frame / per10, rem = frame % per10;
            frame += d * 9L * tens + (rem > d ? d * ((rem - d) / perMin) : 0);
        }
        long ff = frame % nominal, total = frame / nominal;
        return $"{total / 3600 % 24:00}:{total / 60 % 60:00}:{total % 60:00}{(drop ? ';' : ':')}{ff:00}";
    }

    static int DropPerMinute(int nominal) => nominal / 30 * 2;
}
