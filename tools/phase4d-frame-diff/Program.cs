using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Captures a rectangle of the real screen and/or differences captured frames.
//
// `observe` captures a window through its own device context, which does not include what the compositor
// draws on top of it. UIElement.Scale and UIElement.Translation are compositor properties, so a window-space
// capture cannot see a magnified icon at all. This captures the screen instead, where the composited result
// is what the user actually sees.
//
// Usage:
//   phase4d-frames capture <x> <y> <w> <h> <out.png>
//   phase4d-frames diff    <rest.png> <frame.png> [frame.png ...]
//   phase4d-frames cursor

const int SRCCOPY = 0x00CC0020;
const int CAPTUREBLT = 0x40000000;

var command = args.Length > 0 ? args[0] : "";

if (command == "cursor")
{
    GetCursorPos(out var p);
    Console.WriteLine($"{p.X} {p.Y}");
    return 0;
}

if (command == "capture")
{
    var x = int.Parse(args[1]);
    var y = int.Parse(args[2]);
    var w = int.Parse(args[3]);
    var h = int.Parse(args[4]);
    var path = args[5];

    // The pointer is parked over the dock during a sweep, so it would be baked into every frame and swamp the
    // thing being measured. It is hidden for the copy and restored immediately after; ShowCursor is counted,
    // so the matching pair has to be balanced.
    var hidden = 0;
    while (ShowCursor(false) >= 0 && hidden < 8)
    {
        hidden++;
    }

    using (var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb))
    {
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            // CAPTUREBLT is required or layered and composited windows are left out of the copy.
            BitBlt(hdc, 0, 0, w, h, screen, x, y, SRCCOPY | CAPTUREBLT);
            ReleaseDC(IntPtr.Zero, screen);
            g.ReleaseHdc(hdc);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    for (var i = 0; i < hidden; i++)
    {
        ShowCursor(true);
    }

    // A short settle so the cursor is back before the next pointer move is injected.
    Thread.Sleep(60);

    Console.WriteLine($"captured {w}x{h} at ({x},{y}) -> {Path.GetFileName(path)}");
    return 0;
}

if (command != "diff" || args.Length < 3)
{
    if (command == "ink" && args.Length >= 2)
    {
        using var image = new Bitmap(args[1]);
        var tolerance = args.Length > 2 ? int.Parse(args[2]) : 3;

        // The dock's own capture has a light background rather than a pure one, so "drawn" is measured as a
        // departure from the corner pixel rather than as darkness.
        var background = image.GetPixel(0, 0);
        var minX = image.Width;
        var maxX = -1;
        var minY = image.Height;
        var maxY = -1;
        var columns = new int[image.Width];

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var c = image.GetPixel(x, y);
                var d = Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);
                if (d <= tolerance)
                {
                    continue;
                }

                columns[x]++;
                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }
        }

        Console.WriteLine($"image {image.Width}x{image.Height} background {background.R},{background.G},{background.B} tolerance {tolerance}");
        Console.WriteLine($"ink bounds x {minX}..{maxX}  y {minY}..{maxY}");
        Console.WriteLine($"ink width {maxX - minX + 1}  slack left {minX}  slack right {image.Width - 1 - maxX}");

        // Runs of drawn columns, which is where each icon and its label sit.
        var runs = new List<string>();
        var start = -1;
        for (var x = 0; x < image.Width; x++)
        {
            if (columns[x] > 0)
            {
                if (start < 0) { start = x; }
            }
            else if (start >= 0)
            {
                if (x - start > 3) { runs.Add($"{start}..{x - 1}"); }
                start = -1;
            }
        }

        if (start >= 0) { runs.Add($"{start}..{image.Width - 1}"); }
        Console.WriteLine($"drawn runs ({runs.Count}): {string.Join(", ", runs)}");
        return 0;
    }

    Console.Error.WriteLine("usage: phase4d-frames capture <x> <y> <w> <h> <out.png>");
    Console.Error.WriteLine("       phase4d-frames diff    <rest.png> <frame.png> [frame.png ...]");
    Console.Error.WriteLine("       phase4d-frames ink     <image.png> [threshold]");
    Console.Error.WriteLine("       phase4d-frames cursor");
    return 2;
}

using var rest = new Bitmap(args[1]);
var width = rest.Width;
var height = rest.Height;

var (restTop, restBottom) = ContentRows(rest);
Console.WriteLine($"reference {Path.GetFileName(args[1])}  {width}x{height}  drawing rows {restTop}..{restBottom}");
Console.WriteLine();
Console.WriteLine($"{"frame",-16} {"changedPx",9} {"cols",5} {"x range",12} {"top y",6} {"widest run",16} {"runLen",7} {"lift",6}");

foreach (var path in args.Skip(2))
{
    using var frame = new Bitmap(path);

    var changed = 0;
    var minX = width;
    var maxX = -1;
    var minY = height;
    var hits = new int[width];
    var topOfColumn = new int[width];
    Array.Fill(topOfColumn, height);

    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var a = rest.GetPixel(x, y);
            var b = frame.GetPixel(x, y);
            var d = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            if (d <= 20)
            {
                continue;
            }

            changed++;
            hits[x]++;
            if (y < topOfColumn[x]) { topOfColumn[x] = y; }
            if (x < minX) { minX = x; }
            if (x > maxX) { maxX = x; }
            if (y < minY) { minY = y; }
        }
    }

    var runStart = -1;
    var bestStart = -1;
    var bestLength = 0;
    for (var x = 0; x < width; x++)
    {
        if (hits[x] > 1)
        {
            if (runStart < 0) { runStart = x; }
        }
        else if (runStart >= 0)
        {
            if (x - runStart > bestLength) { bestLength = x - runStart; bestStart = runStart; }
            runStart = -1;
        }
    }

    if (runStart >= 0 && width - runStart > bestLength) { bestLength = width - runStart; bestStart = runStart; }

    var highest = height;
    if (bestLength > 0)
    {
        for (var x = bestStart; x < bestStart + bestLength; x++)
        {
            if (topOfColumn[x] < highest) { highest = topOfColumn[x]; }
        }
    }

    var lift = highest < height ? restTop - highest : 0;
    Console.WriteLine(
        $"{Path.GetFileName(path),-16} {changed,9} {hits.Count(h => h > 1),5} "
        + $"{(maxX >= 0 ? $"{minX}..{maxX}" : "-"),12} {(maxX >= 0 ? minY.ToString() : "-"),6} "
        + $"{(bestLength > 0 ? $"{bestStart}..{bestStart + bestLength}" : "-"),16} {bestLength,7} {lift,6}");
}

return 0;

static (int Top, int Bottom) ContentRows(Bitmap bitmap)
{
    var top = bitmap.Height;
    var bottom = -1;

    for (var y = 0; y < bitmap.Height; y++)
    {
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.R >= 245 && c.G >= 245 && c.B >= 245)
            {
                continue;
            }

            if (y < top) { top = y; }
            if (y > bottom) { bottom = y; }
            break;
        }
    }

    return (top, bottom);
}

[DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
[DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
[DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] static extern int ShowCursor(bool show);

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int X; public int Y; }
