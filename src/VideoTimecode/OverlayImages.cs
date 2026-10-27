using System.Collections.Concurrent;

namespace VideoTimecode;

// Where a picture goes when burned in: its top-left corner and scaled size, in video pixels
public sealed record ImagePlacement(string Path, int X, int Y, int W, int H);

// Picture overlays: which files count, and their sizes (cached; safe from any thread)
static class OverlayImages
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public static bool IsImageFile(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    static readonly ConcurrentDictionary<(string, DateTime), Size> Sizes = new();

    // Pixel size of the picture, or null if it is missing or unreadable
    public static Size? SizeOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Sizes.GetOrAdd((path, File.GetLastWriteTimeUtc(path)), _ =>
            {
                using var img = Load(path);
                return img.Size;
            });
        }
        catch { return null; }
    }

    // Loads a copy, so the file isn't kept locked
    public static Bitmap Load(string path)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }

    // Scaled size for a row: height is Size % of the frame height, width keeps the aspect ratio
    public static (double W, double H)? Scaled(Overlay o, double frameHeight)
    {
        if (o.ImagePath == null || SizeOf(o.ImagePath) is not { } size || size.Height == 0) return null;
        double h = Math.Max(1, Math.Round(frameHeight * o.Size / 100));
        return (Math.Max(1, Math.Round(h * size.Width / size.Height)), h);
    }
}
