using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Motion;
using Muralis.Desktop.Icons;

namespace ClearDockPoc;

/// <summary>
/// Route D1 proof of concept: a dock-shaped window that is genuinely transparent where it draws nothing.
/// </summary>
/// <remarks>
/// <para>
/// A native popup with <c>WS_EX_LAYERED</c>, presented through <c>UpdateLayeredWindow</c> from a 32-bit
/// premultiplied ARGB surface. Every frame starts fully transparent, so the empty area between and around the
/// icons is the desktop itself rather than a plate painted to look like it.
/// </para>
/// <para>
/// The icons are the real ones: the shell extraction in <c>Muralis.Desktop</c> is used directly, because a
/// second icon pipeline would be a second thing to be wrong and this proof is about the window, not the icons.
/// The pinned applications come from the product's own settings file, so what is drawn is what the user pinned.
/// </para>
/// <para>
/// <c>--squares</c> swaps the icons for flat colours, which keeps the earlier pixel proof reproducible without
/// depending on the shell.
/// </para>
/// </remarks>
internal static class Program
{
    private const int CellWidth = 56;
    private const int IconBox = 52;
    private const int Padding = 12;
    private const int PlatePaddingY = 10;
    private const int BottomGapDip = 24;
    private const int VerticalReserve = 44;

    [STAThread]
    private static int Main(string[] args)
    {
        var pins = 5;
        var frames = 0;
        var squares = false;
        var alphaSquare = false;
        var topmost = true;
        var forcedScale = 0.0;
        var sweep = false;
        string? pathsFile = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pins" when i + 1 < args.Length:
                    pins = int.Parse(args[++i]);
                    break;
                case "--frames" when i + 1 < args.Length:
                    frames = int.Parse(args[++i]);
                    break;
                case "--squares":
                    squares = true;
                    break;
                case "--alpha-square":
                    alphaSquare = true;
                    break;
                case "--no-topmost":
                    topmost = false;
                    break;
                case "--sweep":
                    sweep = true;
                    break;
                case "--scale" when i + 1 < args.Length:

                    // Taken as a whole percent on purpose: a decimal scale has to be parsed against the right
                    // culture, and a harness on a machine using a comma decimal separator would otherwise pass
                    // 150 and get 15000.
                    forcedScale = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture) / 100.0;
                    break;
                case "--paths" when i + 1 < args.Length:
                    pathsFile = args[++i];
                    break;
            }
        }

        var targets = ResolveTargets(pathsFile, pins);
        pins = targets.Count;
        if (pins == 0)
        {
            Console.Error.WriteLine("no pinned applications to draw");
            return 1;
        }

        // The display's scale. Everything below is stated in DIP and converted once here, which is what makes the
        // dock the same physical size on a 100 % and a 200 % display: at 200 % the stripe is twice as many
        // physical pixels, and the icons are read from the shell at twice the pixel size so they stay sharp.
        // --scale forces a value, because this machine has only a 100 % display and a scale path that has never
        // been executed is a scale path that does not work.
        var scale = forcedScale > 0 ? forcedScale : GetDisplayScale();

        var iconPixels = (int)Math.Round(IconBox * scale);
        var cellWidth = (int)Math.Round(CellWidth * scale);
        var padding = (int)Math.Round(Padding * scale);
        var platePaddingY = (int)Math.Round(PlatePaddingY * scale);
        var verticalReserve = (int)Math.Round(VerticalReserve * scale);
        var iconBox = (int)Math.Round(IconBox * scale);
        var bottomGap = (int)Math.Round(BottomGapDip * scale);

        // The icons are read before the window exists, because the shell calls are synchronous work on another
        // thread and a window that appears and then fills in would make the capture racy.
        var icons = squares ? [] : LoadIcons(targets, iconPixels);

        var screenWidth = GetSystemMetrics(SmCXScreen);
        var screenHeight = GetSystemMetrics(SmCYScreen);

        // The stripe the product geometry defines: the room a magnified icon grows into, the icon box, and the
        // feet. The reserve is real surface, not decoration, so a magnified icon has somewhere to go.
        var width = (pins * cellWidth) - (cellWidth - iconBox) + (2 * padding);
        var height = verticalReserve + iconBox + platePaddingY;

        var exStyle = WsExLayered | WsExToolWindow | WsExNoActivate | (topmost ? WsExTopmost : 0L);
        var hwnd = CreateWindowEx(
            exStyle, "STATIC", "Muralis Clear Dock POC", WsPopup,
            0, 0, width, height, nint.Zero, nint.Zero, nint.Zero, nint.Zero);

        if (hwnd == nint.Zero)
        {
            Console.Error.WriteLine($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            return 2;
        }

        var x = (screenWidth - width) / 2;
        var y = screenHeight - height - bottomGap;
        _ = SetWindowPos(hwnd, topmost ? HwndTopmost : nint.Zero, x, y, width, height, SwpNoActivate | SwpShowWindow);

        var surface = new DibSurface(width, height);

        var screenDc = GetDC(nint.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,          // negative: a top-down DIB, matching the buffer order
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
            },
        };

        var dib = CreateDIBSection(memoryDc, ref bitmapInfo, DibRgbColors, out var bits, nint.Zero, 0);
        if (dib == nint.Zero || bits == nint.Zero)
        {
            Console.Error.WriteLine($"CreateDIBSection failed: {Marshal.GetLastWin32Error()}");
            return 3;
        }

        var previous = SelectObject(memoryDc, dib);

        // Draw one resting frame.
        var metrics = new Metrics(iconBox, cellWidth, padding, platePaddingY, iconPixels);
        ComposeResting(surface, icons, pins, squares, alphaSquare, metrics);
        Marshal.Copy(surface.Pixels, 0, bits, surface.Pixels.Length);

        var destination = new Point(x, y);
        var size = new Size(width, height);
        var source = new Point(0, 0);
        var blend = new BlendFunction
        {
            BlendOp = AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AcSrcAlpha,
        };

        var updated = UpdateLayeredWindow(
            hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        if (!updated)
        {
            Console.Error.WriteLine($"UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()}");
            return 4;
        }

        var drawn = icons.Count(static i => i is not null);
        Console.WriteLine($"hwnd={hwnd} rect=({x},{y}) {width}x{height} pins={pins} iconsLoaded={drawn} squares={squares} topmost={topmost} scale={scale:F2} iconPixels={iconPixels}");
        Console.WriteLine($"UpdateLayeredWindow=ok");
        Console.WriteLine($"targets={string.Join('|', targets)}");
        Console.WriteLine("READY");
        Console.Out.Flush();

        if (frames > 0)
        {
            RunFrameBenchmark(hwnd, screenDc, memoryDc, bits, surface, icons, pins, squares, frames, metrics);
        }

        // The engine sweep is its own mode and returns: it prints what the product's motion engine answers and
        // then exits, so a harness can check the arithmetic instead of inferring it from pixels.
        if (sweep)
        {
            RunMotionSweep(pins, metrics);
            _ = SelectObject(memoryDc, previous);
            _ = DeleteObject(dib);
            _ = DeleteDC(memoryDc);
            _ = ReleaseDC(nint.Zero, screenDc);
            _ = DestroyWindow(hwnd);
            return 0;
        }

        // Live mode: a timer drives hover magnification and click launch. The pointer is polled rather than
        // taken from the process-wide raw input broker, so this proof does not move input ownership; a real
        // renderer would consume the broker, which is already renderer-independent.
        if (!squares)
        {
            Interactive.SetTargets(targets);
            var interactive = new Interactive(hwnd, screenDc, memoryDc, bits, surface, icons, pins, x, y, metrics);
            _ = SetTimer(hwnd, 1, 8, nint.Zero);
            interactive.Start();
            while (GetMessage(out var live, nint.Zero, 0, 0) > 0)
            {
                if (live.Id == WmTimer)
                {
                    interactive.Tick();
                    continue;
                }

                _ = TranslateMessage(ref live);
                _ = DispatchMessage(ref live);
            }

            interactive.Stop();
        }
        else
        {
            while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }

        _ = SelectObject(memoryDc, previous);
        _ = DeleteObject(dib);
        _ = DeleteDC(memoryDc);
        _ = ReleaseDC(nint.Zero, screenDc);
        _ = DestroyWindow(hwnd);
        return 0;
    }

    /// <summary>
    /// The pinned applications, from the product's own settings file unless the caller supplied a list.
    /// </summary>
    private static List<string> ResolveTargets(string? pathsFile, int wanted)
    {
        var result = new List<string>();
        try
        {
            if (pathsFile is not null && File.Exists(pathsFile))
            {
                foreach (var line in File.ReadAllLines(pathsFile))
                {
                    if (!string.IsNullOrWhiteSpace(line) && result.Count < wanted)
                    {
                        result.Add(line.Trim());
                    }
                }

                return result;
            }

            var settings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Muralis", "settings.json");

            if (File.Exists(settings))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settings));
                if (document.RootElement.TryGetProperty("Dock", out var dock)
                    && dock.TryGetProperty("PinnedApps", out var pinned))
                {
                    foreach (var app in pinned.EnumerateArray())
                    {
                        if (result.Count >= wanted)
                        {
                            break;
                        }

                        if (app.TryGetProperty("LaunchTarget", out var target) && target.GetString() is { Length: > 0 } path)
                        {
                            result.Add(path);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not read pinned apps: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// The dock's geometry in physical pixels, derived once from the display's scale.
    /// </summary>
    /// <remarks>
    /// Every number the renderer uses comes from here, so there is exactly one place where DIP becomes pixels.
    /// The whole point is that a 200 % display gets a stripe twice as many physical pixels wide and icons read
    /// from the shell at twice the pixel size — not the same pixel geometry stretched.
    /// </remarks>
    private readonly record struct Metrics(
        int IconBox, int CellWidth, int Padding, int PlatePaddingY, int IconPixels);

    /// <summary>The display's scale, read from the system's DPI for the desktop.</summary>
    private static double GetDisplayScale()
    {
        var dc = GetDC(nint.Zero);
        if (dc == nint.Zero)
        {
            return 1.0;
        }

        try
        {
            var dpi = GetDeviceCaps(dc, LogPixelsX);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        finally
        {
            _ = ReleaseDC(nint.Zero, dc);
        }
    }

    /// <summary>
    /// Reads each application's icon once, through the product's own shell extraction.
    /// </summary>
    private static List<ShellIconData?> LoadIcons(List<string> targets, int iconPixels)
    {
        var icons = new List<ShellIconData?>(targets.Count);
        using var provider = new ShellIconProvider(NullLogger<ShellIconProvider>.Instance);
        foreach (var target in targets)
        {
            try
            {
                icons.Add(provider.GetAsync(target, iconPixels).GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"icon for {target} failed: {ex.Message}");
                icons.Add(null);
            }
        }

        return icons;
    }

    /// <summary>
    /// One resting frame: a fully transparent surface with an icon in each cell, the way the dock rests.
    /// </summary>
    private static void ComposeResting(
        DibSurface surface, List<ShellIconData?> icons, int pins, bool squares, bool alphaSquare, Metrics m)
    {
        surface.Clear();
        var bottom = surface.Height - m.PlatePaddingY;

        for (var i = 0; i < pins; i++)
        {
            var centreX = m.Padding + (i * m.CellWidth) + (m.CellWidth / 2);

            if (squares)
            {
                var size = m.IconBox;
                var left = centreX - (size / 2);
                var top = bottom - size;
                var colour = SquareColours[i % SquareColours.Length];
                // Premultiplied: draw red at half alpha to prove per-pixel alpha rather than one constant.
                var alpha = alphaSquare && i == 0 ? (byte)128 : (byte)255;
                surface.FillRect(
                    left, top, size, size,
                    (byte)(colour.B * alpha / 255), (byte)(colour.G * alpha / 255), (byte)(colour.R * alpha / 255), alpha);
                continue;
            }

            if (icons[i] is { } icon)
            {
                surface.DrawIcon(icon.PremultipliedBgra, icon.Width, icon.Height, centreX, bottom, m.IconBox);
            }
        }
    }

    /// <summary>
    /// Times a realistic motion frame: the whole surface recomposed and uploaded at pointer rate.
    /// </summary>
    /// <remarks>
    /// This is the number that decides whether a layered window is a candidate, so it is measured rather than
    /// assumed. Each frame applies the dock's own motion shape — an icon grows about its bottom centre and rises
    /// by a lift that falls off with distance — and rewrites every pixel, which is the worst case. Reported as
    /// percentiles, because an average hides the stalls a person actually feels.
    /// </remarks>
    private static void RunFrameBenchmark(
        nint hwnd, nint screenDc, nint memoryDc, nint bits, DibSurface surface,
        List<ShellIconData?> icons, int pins, bool squares, int frames, Metrics m)
    {
        var times = new double[frames];
        var stopwatch = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var bottom = surface.Height - m.PlatePaddingY;

        for (var frame = 0; frame < frames; frame++)
        {
            var phase = frame * 0.08;
            var start = stopwatch.Elapsed.TotalMilliseconds;
            surface.Clear();

            for (var i = 0; i < pins; i++)
            {
                var influence = Math.Exp(-Math.Pow(i - ((pins - 1) / 2.0) - (2.0 * Math.Sin(phase)), 2) / 2.0);
                var scale = 1.0 + (0.8 * influence);
                var lift = 10.0 * influence * influence;
                var box = (int)(m.IconBox * scale);
                var centreX = m.Padding + (i * m.CellWidth) + (m.CellWidth / 2);
                var iconBottom = bottom - (int)lift;

                if (squares)
                {
                    var colour = SquareColours[i % SquareColours.Length];
                    surface.FillRect(
                        centreX - (box / 2), iconBottom - box, box, box, colour.B, colour.G, colour.R, 255);
                }
                else if (icons[i] is { } icon)
                {
                    surface.DrawIcon(icon.PremultipliedBgra, icon.Width, icon.Height, centreX, iconBottom, box);
                }
            }

            Marshal.Copy(surface.Pixels, 0, bits, surface.Pixels.Length);

            var destination = new Point(0, 0);
            var size = new Size(surface.Width, surface.Height);
            var source = new Point(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };

            _ = UpdateLayeredWindow(hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
            times[frame] = stopwatch.Elapsed.TotalMilliseconds - start;
        }

        stopwatch.Stop();
        var ordered = (double[])times.Clone();
        Array.Sort(ordered);
        var total = 0.0;
        foreach (var t in times)
        {
            total += t;
        }

        Console.WriteLine($"BENCH frames={frames} pins={pins} surface={surface.Width}x{surface.Height} squares={squares}");
        Console.WriteLine($"BENCH p50={Percentile(ordered, 0.50):F3}ms p95={Percentile(ordered, 0.95):F3}ms p99={Percentile(ordered, 0.99):F3}ms max={ordered[^1]:F3}ms mean={total / frames:F3}ms");
        Console.WriteLine($"BENCH impliedMaxFps={1000.0 / Percentile(ordered, 0.95):F0} (from p95, one upload per frame)");
        Console.WriteLine($"BENCH workingSetMB={process.WorkingSet64 / (1024.0 * 1024.0):F1} privateMB={process.PrivateMemorySize64 / (1024.0 * 1024.0):F1}");
        Console.Out.Flush();
    }

    /// <summary>
    /// Walks the pointer across the dock and prints what the product's motion engine answers at each step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the Nexus proof. The interaction harness can only see the peak scale, which says the engine got
    /// to 1.8 but nothing about the shape of the wave. Here the engine is driven directly over the whole span —
    /// every icon centre and every gap between them — and every sample is printed, so the properties that make
    /// the dock feel like a dock can be checked rather than assumed:
    /// </para>
    /// <list type="bullet">
    /// <item>the scale under the pointer is exactly the profile's maximum, and falls off both ways;</item>
    /// <item>the largest icon does not move, and the ones beside it are pushed outwards, never inwards;</item>
    /// <item>the pitch between neighbours is preserved, so magnification opens the run rather than bursting it;</item>
    /// <item>away from the wave everything is exactly at rest.</item>
    /// </list>
    /// </remarks>
    private static void RunMotionSweep(int pins, Metrics m)
    {
        var motion = new DockMotionState(pins, m.CellWidth, m.IconBox, m.Padding);
        var profile = DockMotionProfile.Default;

        Console.WriteLine($"SWEEP pins={pins} iconBox={m.IconBox} cell={m.CellWidth} pad={m.Padding}");
        Console.WriteLine($"SWEEP profile base={profile.BaseIconSize} spacing={profile.SpacingDip} maxScale={profile.MaxScale} radius={profile.InfluenceRadius} lift={profile.MaximumLift} spread={profile.NeighbourSpread}");

        // The span the pointer can occupy: every icon centre, and every gap between two of them. The gaps matter
        // as much as the centres because the worst case for the sideways push is a pointer parked between two
        // icons, where both sides of the run take their share at once.
        var stops = new List<double>();
        for (var i = 0; i < pins; i++)
        {
            stops.Add(motion.Centres[i]);
            if (i + 1 < pins)
            {
                stops.Add((motion.Centres[i] + motion.Centres[i + 1]) / 2.0);
            }
        }

        foreach (var pointer in stops)
        {
            motion.Update(pointer);
            var parts = new List<string>(pins);
            for (var i = 0; i < pins; i++)
            {
                var s = motion.Sample(i);
                parts.Add(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"i{i}:s{s.Scale:F6},x{s.TranslateX:F6},l{s.Lift:F6}"));
            }

            Console.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"SWEEP pointer={pointer:F3} peak={motion.PeakScale:F6} active={motion.IsActive} {string.Join(' ', parts)}"));
        }

        // And the resting answer, which is what the dock must return to when the pointer leaves.
        motion.Update(null);
        var rest = new List<string>(pins);
        for (var i = 0; i < pins; i++)
        {
            var s = motion.Sample(i);
            rest.Add(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"i{i}:s{s.Scale:F6},x{s.TranslateX:F6},l{s.Lift:F6}"));
        }

        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"SWEEP pointer=away peak={motion.PeakScale:F6} active={motion.IsActive} {string.Join(' ', rest)}"));
        Console.WriteLine("SWEEP done");
        Console.Out.Flush();
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    /// <summary>
    /// Hover magnification and click launch, driven by the product's own motion engine.
    /// </summary>
    /// <remarks>
    /// It reports what it did on stdout so a harness can check the behaviour rather than take a screenshot's
    /// word for it: every peak the engine reaches and every launch it performs.
    /// </remarks>
    private sealed class Interactive
    {
        private readonly nint _hwnd;
        private readonly nint _screenDc;
        private readonly nint _memoryDc;
        private readonly nint _bits;
        private readonly DibSurface _surface;
        private readonly List<ShellIconData?> _icons;
        private readonly int _pins;
        private readonly int _windowX;
        private readonly int _windowY;
        private readonly DockMotionState _motion;

        private readonly Metrics _metrics;
        private bool _pointerWasDown;
        private double _peakSeen;
        private int _lastHit = -1;

        public Interactive(
            nint hwnd, nint screenDc, nint memoryDc, nint bits, DibSurface surface,
            List<ShellIconData?> icons, int pins, int windowX, int windowY, Metrics m)
        {
            _hwnd = hwnd;
            _screenDc = screenDc;
            _memoryDc = memoryDc;
            _bits = bits;
            _surface = surface;
            _icons = icons;
            _pins = pins;
            _windowX = windowX;
            _windowY = windowY;
            _metrics = m;
            _motion = new DockMotionState(pins, m.CellWidth, m.IconBox, m.Padding);
        }

        public void Start() => Console.WriteLine("INTERACTIVE ready");

        public void Stop()
        {
            _ = KillTimer(_hwnd, 1);
            Console.WriteLine($"INTERACTIVE peak={_peakSeen:F4}");
            Console.Out.Flush();
        }

        /// <summary>One frame: read the pointer, move the wave, redraw, present, and act on a click.</summary>
        public void Tick()
        {
            if (!GetCursorPos(out var cursor))
            {
                return;
            }

            // Screen pixels to the dock's own space. One pixel per DIP is assumed here because this proof runs
            // on a single monitor at 100%; the product converts through the window's own scale.
            var x = cursor.X - _windowX;
            var y = cursor.Y - _windowY;

            var overDock = x >= 0 && x < _surface.Width && y >= 0 && y < _surface.Height;
            _motion.Update(overDock ? x : null);

            var peak = _motion.PeakScale;
            if (peak > _peakSeen)
            {
                _peakSeen = peak;
                Console.WriteLine($"INTERACTIVE peak={peak:F4} pointer={x}");
                Console.Out.Flush();
            }

            Compose(_motion);
            Present();

            var hit = _motion.HitTest(x, y, _metrics.IconBox, _surface.Height, _metrics.PlatePaddingY, 0);
            if (hit != _lastHit)
            {
                _lastHit = hit;
                Console.WriteLine($"INTERACTIVE hover={hit}");
                Console.Out.Flush();
            }

            var down = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
            if (_pointerWasDown && !down && overDock && hit >= 0)
            {
                Launch(hit);
            }

            _pointerWasDown = down;
        }

        /// <summary>
        /// Draws the current wave. Nothing is painted where there is no icon, so the desktop stays visible.
        /// </summary>
        private void Compose(DockMotionState motion)
        {
            _surface.Clear();
            var restingBottom = _surface.Height - _metrics.PlatePaddingY;

            for (var i = 0; i < _pins; i++)
            {
                if (_icons[i] is not { } icon)
                {
                    continue;
                }

                var sample = motion.Sample(i);
                var box = (int)Math.Round(_metrics.IconBox * sample.Scale);
                var centreX = (int)Math.Round(motion.Centres[i] + sample.TranslateX);
                var bottom = restingBottom - (int)Math.Round(sample.Lift);
                _surface.DrawIcon(icon.PremultipliedBgra, icon.Width, icon.Height, centreX, bottom, box);
            }
        }

        private void Present()
        {
            Marshal.Copy(_surface.Pixels, 0, _bits, _surface.Pixels.Length);
            var destination = new Point(_windowX, _windowY);
            var size = new Size(_surface.Width, _surface.Height);
            var source = new Point(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };

            _ = UpdateLayeredWindow(
                _hwnd, _screenDc, ref destination, ref size, _memoryDc, ref source, 0, ref blend, UlwAlpha);
        }

        private void Launch(int index)
        {
            var target = _targets[index];
            Console.WriteLine($"INTERACTIVE launch={index} target={target}");
            Console.Out.Flush();
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"INTERACTIVE launchFailed={index} error={ex.Message}");
                Console.Out.Flush();
            }
        }

        private static List<string> _targets = [];

        public static void SetTargets(List<string> targets) => _targets = targets;
    }

    private static readonly (byte R, byte G, byte B)[] SquareColours =
    [
        (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255),
        (0, 255, 255), (128, 128, 128), (255, 128, 0), (128, 0, 255), (0, 128, 64),
    ];

    private const int SmCXScreen = 0;
    private const int SmCYScreen = 1;
    private const long WsExLayered = 0x00080000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExTopmost = 0x00000008L;
    private const uint WsPopup = 0x80000000;
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const uint UlwAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;

        public Size(int cx, int cy)
        {
            Cx = cx;
            Cy = cy;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colours;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        long exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);

    private const int LogPixelsX = 88;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint hwnd, nuint id);

    private const int VkLeftButton = 0x01;
    private const uint WmTimer = 0x0113;

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        nint hwnd, nint destinationDc, ref Point destination, ref Size size,
        nint sourceDc, ref Point source, uint colourKey, ref BlendFunction blend, uint flags);
}
