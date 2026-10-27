namespace VideoTimecode;

// How the rows at one position line up with each other
public enum RowAlign { Auto, Left, Center, Right }

// One overlay row, sized for layout: line height px, number of lines, box padding, widest line
readonly record struct RowSize(double Px, int Lines, double Pad, double Width);

// Where a row goes: its text block's top-left/top-center/top-right corner (ASS \an7/8/9)
readonly record struct RowPlacement(double X, double Y, int An);

// Stacks the rows at one position, in list order, inside the frame. Used by both the
// .ass writer and the preview so they agree.
static class OverlayLayout
{
    public static double Margin(double frameHeight) => frameHeight / 30;

    public static RowAlign Effective(Anchor9 pos, RowAlign align) =>
        align != RowAlign.Auto ? align : (((int)pos - 1) % 3) switch { 0 => RowAlign.Left, 1 => RowAlign.Center, _ => RowAlign.Right };

    public static List<RowPlacement> Place(Anchor9 pos, RowAlign align, IReadOnlyList<RowSize> rows, double w, double h)
    {
        double margin = Margin(h);
        int col = ((int)pos - 1) % 3, band = ((int)pos - 1) / 3;   // col: 0 left 1 center 2 right; band: 0 bottom 1 middle 2 top
        var a = Effective(pos, align);

        double total = rows.Sum(r => r.Lines * r.Px + 2 * r.Pad);
        double y = band switch { 2 => margin, 1 => (h - total) / 2, _ => h - margin - total };

        // Auto anchors each row at the margin (or centre); otherwise rows share the edges of a
        // block as wide as the widest row, which sits at the position's side of the frame
        double x;
        if (align == RowAlign.Auto) x = col switch { 0 => margin, 1 => w / 2, _ => w - margin };
        else
        {
            double blockW = rows.Count > 0 ? rows.Max(r => r.Width) : 0;
            double left = col switch { 0 => margin, 1 => (w - blockW) / 2, _ => w - margin - blockW };
            x = a switch { RowAlign.Left => left, RowAlign.Center => left + blockW / 2, _ => left + blockW };
        }
        int an = a switch { RowAlign.Left => 7, RowAlign.Center => 8, _ => 9 };

        var result = new List<RowPlacement>(rows.Count);
        foreach (var r in rows)
        {
            result.Add(new RowPlacement(x, y + r.Pad, an));
            y += r.Lines * r.Px + 2 * r.Pad;
        }
        return result;
    }
}
