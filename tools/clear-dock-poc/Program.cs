using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Core.Motion;
using Muralis.Desktop.Icons;
using Muralis.Desktop.Input;

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
    /// <summary>
    /// How many raw reports may wait for the window's thread before the oldest are abandoned.
    /// </summary>
    /// <remarks>
    /// Large enough that an ordinary burst of reports is never dropped, small enough that a consumer which has
    /// stopped draining is a bounded loss rather than a growing backlog. The product's own dock uses a queue of
    /// the same order for the same reason.
    /// </remarks>
    private const int PointerQueueCapacity = 256;

    /// <summary>
    /// How many distinct icon pixel sizes each icon keeps resampled.
    /// </summary>
    /// <remarks>
    /// The magnification moves through a continuum of scales but draws at whole pixels, so the sizes actually
    /// needed at any moment are a narrow band 闁?a few dozen across the whole 1.0 to 1.8 range at one display
    /// scale. This is comfortably above that band and still a hard bound, and a cache empties itself rather than
    /// growing when a display change moves the band somewhere else entirely.
    /// </remarks>
    /// <summary>
    /// How many distinct icon pixel sizes each icon keeps resampled.
    /// </summary>
    /// <remarks>
    /// Sized from the geometry rather than guessed. The magnification spans <c>MaxScale</c> of the resting box,
    /// so at 100 % the drawn sizes run from 52 to 94 pixels; at 200 % they run from 104 to 188. This is the wider
    /// of those spans with room to spare, and the cache is prewarmed to fill it, so the bound is never reached in
    /// normal running and no frame is ever the one that pays for a resample.
    /// </remarks>
    private const int IconScalerCapacity = 256;

    /// <summary>
    /// The counters as they were last written, so an unchanged reading is not repeated.
    /// </summary>
    /// <remarks>
    /// <c>Reports</c> counts <c>WM_INPUT</c> messages and <c>Queued</c> counts the ones that carried a new cursor
    /// position, so the first is legitimately the larger: a report that does not move the cursor is the mouse
    /// saying nothing new, and the source deliberately does not raise one. Both are printed together so that
    /// difference is visible rather than looking like a lost sample.
    /// </remarks>
    private readonly record struct RawCounters(
        long Reports, long Queued, long Dropped, int Frames, int Applies);

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
        var reportCounters = false;
        var probeStall = false;
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
                case "--report-counters":
                    reportCounters = true;
                    break;
                case "--probe-stall":
                    probeStall = true;
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

        // The display's scale. Every geometric number below is derived from it exactly once, in DockMetrics, so
        // that DIP stays DIP and pixels stay pixels for the whole renderer. --scale forces a value, because a
        // scale path that has never been executed is a scale path that does not work.
        var scale = forcedScale > 0 ? forcedScale : GetDisplayScale();
        var metrics = new DockMetrics(scale);

        // Artwork is read at the largest size this icon can ever be drawn at, so no frame ever goes back to the
        // shell and no frame has to invent detail by scaling the resting artwork up.
        var artworkPx = metrics.ArtworkPx(DockMotionProfile.Default.MaxScale);

        // The icons are read before the window exists, because the shell calls are synchronous work on another
        // thread and a window that appears and then fills in would make the capture racy.
        var icons = squares ? [] : LoadIcons(targets, artworkPx);

        // Each icon gets its own artwork cache. Sharing one and keying it by the source's dimensions is wrong in
        // a way that is easy to miss: every shell icon is the same size, so every icon would be handed the first
        // icon's pixels. That defect drew five copies of one logo while the counters reported five icons drawn.
        var artwork = new IconArtwork?[icons.Count];
        for (var i = 0; i < icons.Count; i++)
        {
            artwork[i] = icons[i] is { } icon ? new IconArtwork(icon, IconScalerCapacity) : null;
        }

        // Every size the magnification can reach is resampled now, at startup, rather than inside whichever frame
        // first needs it. This is the measured fix for a 13.5 ms worst frame whose cost was almost entirely in the
        // raster stage, and it is what makes the remaining frames uniform: with every size resident, no frame can
        // miss the cache and pay for a filter.
        var smallestDrawnPx = metrics.IconBoxPx;
        var largestDrawnPx = (int)Math.Ceiling(DockMetrics.IconBoxDip * scale * DockMotionProfile.Default.MaxScale);
        foreach (var art in artwork)
        {
            art?.Prewarm(smallestDrawnPx, largestDrawnPx, 1);
        }

        if (!squares)
        {
            long prewarmBytes = 0;
            var prewarmSizes = 0;
            foreach (var art in artwork)
            {
                if (art is null)
                {
                    continue;
                }

                prewarmBytes += art.CachedBytes;
                prewarmSizes += art.CachedSizes;
            }

            Console.WriteLine(
                $"ARTWORK sizes={prewarmSizes} bytes={prewarmBytes} perIconPx={smallestDrawnPx}..{largestDrawnPx} source={artwork[0]?.SourceWidth}px");
        }

        var screenWidth = GetSystemMetrics(SmCXScreen);
        var screenHeight = GetSystemMetrics(SmCYScreen);

        var width = metrics.StripeWidthPx(pins);
        var height = metrics.StripeHeightPx;
        var x = (screenWidth - width) / 2;
        var y = screenHeight - height - metrics.BottomGapPx;

        // The pointer arrives on the raw source's thread and the frame is composed on this one, so the adapter
        // queues reports and posts this window once per batch. The queue is built before the window because the
        // window's procedure is what drains it.
        var pointerQueue = new PointerQueue(PointerQueueCapacity);

        // The window's procedure runs on this same thread, inside the message pump below, so a batch posted
        // before the renderer exists would be drained only after the pump starts 闁?by which time this is set.
        Interactive? interactive = null;

        var exStyle = WsExLayered | WsExToolWindow | WsExNoActivate | (topmost ? WsExTopmost : 0L);
        var hwnd = NativeWindowHost.Create(
            exStyle, "Muralis Clear Dock POC", x, y, width, height,
            () => interactive?.DrainPointerBatch());

        if (hwnd == nint.Zero)
        {
            Console.Error.WriteLine($"window creation failed: {Marshal.GetLastWin32Error()}");
            return 2;
        }

        var space = new CoordinateSpace(scale, x, y);
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
        ComposeResting(surface, artwork, pins, squares, alphaSquare, metrics);
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
        Console.WriteLine($"hwnd={hwnd} rect=({x},{y}) {width}x{height} pins={pins} iconsLoaded={drawn} squares={squares} topmost={topmost} scale={scale:F2} iconPixels={metrics.IconBoxPx} artwork={artworkPx}");
        Console.WriteLine($"UpdateLayeredWindow=ok");
        Console.WriteLine($"targets={string.Join('|', targets)}");
        Console.WriteLine("READY");
        Console.Out.Flush();

        if (frames > 0)
        {
            RunFrameBenchmark(hwnd, screenDc, memoryDc, bits, surface, artwork, pins, squares, frames, metrics, probeStall);
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

        // Live mode. Motion is driven by the process-wide RawPointerBroker, which owns the single native raw
        // input registration: this renderer is a consumer of it and never registers a device class itself. There
        // is no timer and no GetCursorPos anywhere on this path 闁?a frame is composed when a report arrives.
        if (!squares)
        {
            Interactive.SetTargets(targets);
            interactive = new Interactive(
                hwnd, screenDc, memoryDc, bits, surface, artwork, pins, space, metrics, artworkPx, pointerQueue);
            interactive.Start();

            // A harness that kills this process never reaches Stop, so the counters a probe most needs to read 闁?            // whether reports arrive at all, and whether they were kept up with 闁?would be lost exactly when they
            // matter. The periodic report is opt-in and exists only for measurement: it is a diagnostic, not a
            // frame clock, and with the flag absent there is no timer in this process at all.
            var reported = new RawCounters(-1, -1, -1, -1, -1);
            if (reportCounters)
            {
                _ = SetTimer(hwnd, CounterTimerId, CounterReportMilliseconds, nint.Zero);
            }

            while (GetMessage(out var live, nint.Zero, 0, 0) > 0)
            {
                if (reportCounters && live.Id == WmTimer && live.WParam == CounterTimerId)
                {
                    reported = interactive.ReportCountersIfChanged(reported);
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
        DibSurface surface, IconArtwork?[] artwork, int pins,
        bool squares, bool alphaSquare, DockMetrics m)
    {
        surface.Clear();
        var bottom = m.RestingBaselinePx;

        for (var i = 0; i < pins; i++)
        {
            var centreX = m.PaddingPx + (i * m.CellPx) + (m.CellPx / 2);

            if (squares)
            {
                var size = m.IconBoxPx;
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

            if (artwork[i] is { } art)
            {
                surface.DrawIconScaled(art, centreX, bottom, m.IconBoxPx);
            }
        }
    }

    /// <summary>
    /// Times a realistic motion frame, split into the stages a stall could be hiding in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each frame applies the dock's own motion shape 闁?an icon grows about its bottom centre and rises by a lift
    /// that falls off with distance 闁?and rewrites every pixel, which is the worst case. Reported as percentiles,
    /// because an average hides the stalls a person actually feels.
    /// </para>
    /// <para>
    /// Every frame is timed in stages rather than as one number. A single ~10 ms maximum per run has been
    /// observed since this benchmark existed and could not be attributed; a total that spikes tells you a frame
    /// was slow, while a stage breakdown tells you <em>which</em> of raster, copy, upload or the runtime was slow.
    /// The per-frame allocation counter is taken because a stall that is really a garbage collection shows up
    /// there and nowhere else.
    /// </para>
    /// </remarks>
    private static void RunFrameBenchmark(
        nint hwnd, nint screenDc, nint memoryDc, nint bits, DibSurface surface,
        IconArtwork?[] artwork, int pins, bool squares, int frames, DockMetrics m, bool probeStall)
    {
        var totals = new double[frames];
        var clears = new double[frames];
        var rasters = new double[frames];
        var copies = new double[frames];
        var uploads = new double[frames];

        var stopwatch = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var bottom = m.RestingBaselinePx;

        // Sampled before the loop and after it, so the difference is what the benchmark itself allocated.
        var gcBefore = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var worstFrame = -1;
        var worstTotal = 0.0;

        for (var frame = 0; frame < frames; frame++)
        {
            var phase = frame * 0.08;
            var frameStart = stopwatch.Elapsed.TotalMilliseconds;
            var mark = frameStart;

            surface.Clear();
            var t1 = stopwatch.Elapsed.TotalMilliseconds;
            clears[frame] = t1 - mark;

            for (var i = 0; i < pins; i++)
            {
                var influence = Math.Exp(-Math.Pow(i - ((pins - 1) / 2.0) - (2.0 * Math.Sin(phase)), 2) / 2.0);
                var scale = 1.0 + (0.8 * influence);
                var lift = 10.0 * influence * influence;
                var box = (int)(m.IconBoxPx * scale);
                var centreX = m.PaddingPx + (i * m.CellPx) + (m.CellPx / 2);
                var iconBottom = bottom - (int)lift;

                if (squares)
                {
                    var colour = SquareColours[i % SquareColours.Length];
                    surface.FillRect(
                        centreX - (box / 2), iconBottom - box, box, box, colour.B, colour.G, colour.R, 255);
                }
                else if (artwork[i] is { } art)
                {
                    surface.DrawIconScaled(art, centreX, iconBottom, box);
                }
            }

            var t2 = stopwatch.Elapsed.TotalMilliseconds;
            rasters[frame] = t2 - t1;

            Marshal.Copy(surface.Pixels, 0, bits, surface.Pixels.Length);
            var t3 = stopwatch.Elapsed.TotalMilliseconds;
            copies[frame] = t3 - t2;

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
            var t4 = stopwatch.Elapsed.TotalMilliseconds;
            uploads[frame] = t4 - t3;

            totals[frame] = t4 - frameStart;
            if (totals[frame] > worstTotal)
            {
                worstTotal = totals[frame];
                worstFrame = frame;
            }
        }

        stopwatch.Stop();

        // A hypothesis test for the one slow frame every run produces. The worst frame lands on the same index
        // every time, which is not what an external stall looks like, so the frames are run again with a short
        // blocking sleep in the middle. A sleep enters the kernel and lets the scheduler do the bookkeeping it
        // otherwise defers; if the stall is that deferred work, it disappears here. If it survives, it is
        // something about this loop and not the operating system's clock.
        if (probeStall && frames >= 3000)
        {
            ProbeSleepStall(hwnd, screenDc, memoryDc, bits, surface, artwork, pins, squares, m);
        }

        var gcAfter = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);

        Console.WriteLine($"BENCH frames={frames} pins={pins} surface={surface.Width}x{surface.Height} squares={squares}");
        Report("BENCH total", totals, frames);
        Report("BENCH clear", clears, frames);
        Report("BENCH raster", rasters, frames);
        Report("BENCH copy", copies, frames);
        Report("BENCH upload", uploads, frames);
        Console.WriteLine($"BENCH impliedMaxFps={1000.0 / Percentile(Sorted(totals), 0.95):F0} (from p95, one upload per frame)");
        Console.WriteLine($"BENCH worstFrame={worstFrame} worstTotal={worstTotal:F3}ms worstStages=clear {clears[worstFrame]:F3} raster {rasters[worstFrame]:F3} copy {copies[worstFrame]:F3} upload {uploads[worstFrame]:F3}");
        var cachedSizes = 0;
        var hits = 0L;
        var misses = 0L;
        foreach (var art in artwork)
        {
            if (art is null)
            {
                continue;
            }

            cachedSizes += art.CachedSizes;
            hits += art.Hits;
            misses += art.Misses;
        }

        Console.WriteLine($"BENCH gcCollections={gcAfter - gcBefore} allocatedKB={(allocatedAfter - allocatedBefore) / 1024.0:F1} cacheHits={hits} cacheMisses={misses} cachedSizes={cachedSizes}");
        Console.WriteLine($"BENCH workingSetMB={process.WorkingSet64 / (1024.0 * 1024.0):F1} privateMB={process.PrivateMemorySize64 / (1024.0 * 1024.0):F1}");
        Console.Out.Flush();
    }

    /// <summary>
    /// Runs the same frames with a blocking sleep in the middle, to see whether the one slow frame survives.
    /// </summary>
    /// <remarks>
    /// The worst frame has landed on the same index in every run, which is the signature of something with a
    /// fixed period rather than a random stall. Windows rounds a thread's scheduling quantum up to the system
    /// timer tick 閳?classically 15.625 ms 閳?the first time it blocks, and a thread that never blocks never pays
    /// it. If that is the cause, a sleep in the middle of the run absorbs the charge and the slow frame is gone.
    /// </remarks>
    private static void ProbeSleepStall(
        nint hwnd, nint screenDc, nint memoryDc, nint bits, DibSurface surface,
        IconArtwork?[] artwork, int pins, bool squares, DockMetrics m)
    {
        const int ProbeFrames = 3000;
        const int SleepAtFrame = 1562;
        var times = new double[ProbeFrames];
        var bottom = m.RestingBaselinePx;

        // Its own stopwatch, started here: the benchmark's own has already been stopped by the time this runs,
        // and timing against a stopped clock reads every frame as instantaneous.
        var clock = Stopwatch.StartNew();

        for (var frame = 0; frame < ProbeFrames; frame++)
        {
            if (frame == SleepAtFrame)
            {
                // A real wait, not a spin: this is the call that lets the scheduler settle its accounting.
                Thread.Sleep(1);
            }

            var phase = frame * 0.08;
            var frameStart = clock.Elapsed.TotalMilliseconds;

            surface.Clear();
            for (var i = 0; i < pins; i++)
            {
                var influence = Math.Exp(-Math.Pow(i - ((pins - 1) / 2.0) - (2.0 * Math.Sin(phase)), 2) / 2.0);
                var scale = 1.0 + (0.8 * influence);
                var lift = 10.0 * influence * influence;
                var box = (int)(m.IconBoxPx * scale);
                var centreX = m.PaddingPx + (i * m.CellPx) + (m.CellPx / 2);
                var iconBottom = bottom - (int)lift;

                if (squares)
                {
                    var colour = SquareColours[i % SquareColours.Length];
                    surface.FillRect(
                        centreX - (box / 2), iconBottom - box, box, box, colour.B, colour.G, colour.R, 255);
                }
                else if (artwork[i] is { } art)
                {
                    surface.DrawIconScaled(art, centreX, iconBottom, box);
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
            times[frame] = clock.Elapsed.TotalMilliseconds - frameStart;
        }

        var ordered = Sorted(times);
        var worst = 0;
        for (var i = 1; i < times.Length; i++)
        {
            if (times[i] > times[worst])
            {
                worst = i;
            }
        }

        Console.WriteLine(
            $"BENCH sleepProbe frames={ProbeFrames} sleptAt={SleepAtFrame} p50={Percentile(ordered, 0.50):F3}ms p95={Percentile(ordered, 0.95):F3}ms p99={Percentile(ordered, 0.99):F3}ms max={ordered[^1]:F3}ms worstFrame={worst} elapsed={clock.Elapsed.TotalMilliseconds:F0}ms");
        Console.Out.Flush();
    }

    private static double[] Sorted(double[] values)
    {
        var ordered = (double[])values.Clone();
        Array.Sort(ordered);
        return ordered;
    }

    private static void Report(string label, double[] values, int frames)
    {
        var ordered = Sorted(values);
        var total = 0.0;
        foreach (var v in values)
        {
            total += v;
        }

        Console.WriteLine(
            $"{label} p50={Percentile(ordered, 0.50):F3}ms p95={Percentile(ordered, 0.95):F3}ms p99={Percentile(ordered, 0.99):F3}ms max={ordered[^1]:F3}ms mean={total / frames:F3}ms");
    }

    /// <summary>
    /// Walks the pointer across the dock and prints what the product's motion engine answers at each step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the Nexus proof. The interaction harness can only see the peak scale, which says the engine got
    /// to 1.8 but nothing about the shape of the wave. Here the engine is driven directly over the whole span 闁?    /// every icon centre and every gap between them 闁?and every sample is printed, so the properties that make
    /// the dock feel like a dock can be checked rather than assumed:
    /// </para>
    /// <list type="bullet">
    /// <item>the scale under the pointer is exactly the profile's maximum, and falls off both ways;</item>
    /// <item>the largest icon does not move, and the ones beside it are pushed outwards, never inwards;</item>
    /// <item>the pitch between neighbours is preserved, so magnification opens the run rather than bursting it;</item>
    /// <item>away from the wave everything is exactly at rest.</item>
    /// </list>
    /// </remarks>
    private static void RunMotionSweep(int pins, DockMetrics m)
    {
        var motion = new DockMotionState(pins, m.CellPx, m.IconBoxPx, m.PaddingPx);
        var profile = DockMotionProfile.Default;

        Console.WriteLine($"SWEEP pins={pins} iconBox={m.IconBoxPx} cell={m.CellPx} pad={m.PaddingPx}");
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
    /// The live dock: pointer reports in, motion engine, one composed frame out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pointer comes from the process-wide <see cref="RawPointerBroker"/>. This class is a consumer of that
    /// broker and does not register a raw input device class, open a second raw window, or install a hook 闁?the
    /// broker already owns the single registration, and a second owner is the specific failure this arrangement
    /// exists to prevent. There is no timer and no <c>GetCursorPos</c>: a frame happens because a report arrived.
    /// </para>
    /// <para>
    /// It reports what it did on stdout so a harness can check the behaviour rather than take a screenshot's
    /// word for it: every peak the engine reaches, every hover transition, every launch, and the counters that
    /// say whether reports were kept up with.
    /// </para>
    /// </remarks>
    private sealed class Interactive
    {
        private readonly nint _hwnd;
        private readonly nint _screenDc;
        private readonly nint _memoryDc;
        private readonly nint _bits;
        private readonly DibSurface _surface;
        private readonly IconArtwork?[] _artwork;
        private readonly int _pins;
        private readonly CoordinateSpace _space;
        private readonly DockMetrics _metrics;
        private readonly int _artworkPx;
        private readonly PointerQueue _queue;
        private readonly DockMotionState _motion;

        /// <summary>The order the icons are drawn in. Reordering changes this and nothing else.</summary>
        private readonly DockOrder _order;

        private readonly DockDrag _drag;

        /// <summary>Where each slot rests, in DIP. Fixed by the layout, independent of the order.</summary>
        private readonly double[] _restingCentresDip;

        private readonly RawPointerBroker _broker = new();
        private IDisposable? _subscription;

        private readonly List<PointerSample> _batch = new(PointerQueueCapacity);
        private readonly PointerButtonState _buttons = new();

        private bool _pointerWasDown;
        private double _peakSeen;
        private int _lastHit = -1;
        private int _lastCandidate = -2;

        /// <summary>The slot the button went down on, or -1. A click is decided then, not when it is released.</summary>
        private int _pressedSlot = -1;

        public Interactive(
            nint hwnd, nint screenDc, nint memoryDc, nint bits, DibSurface surface,
            IconArtwork?[] artwork, int pins, CoordinateSpace space, DockMetrics metrics,
            int artworkPx, PointerQueue queue)
        {
            _hwnd = hwnd;
            _screenDc = screenDc;
            _memoryDc = memoryDc;
            _bits = bits;
            _surface = surface;
            _artwork = artwork;
            _pins = pins;
            _space = space;
            _metrics = metrics;
            _artworkPx = artworkPx;
            _queue = queue;
            _motion = new DockMotionState(pins, metrics.CellPx, metrics.IconBoxPx, metrics.PaddingPx);

            _order = new DockOrder(pins);
            _restingCentresDip = new double[pins];
            for (var slot = 0; slot < pins; slot++)
            {
                _restingCentresDip[slot] = _motion.Centres[slot];
            }

            _drag = new DockDrag(_order, DockMotionProfile.Default, _restingCentresDip);
        }

        public void Start()
        {
            // Subscribing is what opens the broker's single raw input registration. Reports then arrive on the
            // raw source's own thread, which is why the handler does nothing but queue them.
            _subscription = _broker.Subscribe(OnPointerReport);

            Console.WriteLine(
                $"INTERACTIVE ready brokerRegistered={_broker.IsRegistered} consumers={_broker.ConsumerCount} queueCapacity={PointerQueueCapacity}");
            Console.Out.Flush();
        }

        public void Stop()
        {
            _subscription?.Dispose();
            _subscription = null;
            _broker.Dispose();
            _queue.Discard();

            Console.WriteLine($"INTERACTIVE peak={_peakSeen:F4}");
            WriteCounters("final", Snapshot());
        }

        /// <summary>
        /// Writes the counters when they have moved since the last reading, and returns the reading.
        /// </summary>
        /// <remarks>
        /// Measurement only, and opt-in. Without it a harness that kills this process would see no counters at
        /// all, which is the one case where "no reports arrived" and "reports arrived and nothing happened" are
        /// indistinguishable 闁?and they need opposite fixes.
        /// </remarks>
        public RawCounters ReportCountersIfChanged(RawCounters previous)
        {
            var current = Snapshot();
            if (current == previous)
            {
                return previous;
            }

            WriteCounters("counters", current);
            return current;
        }

        /// <summary>
        /// Every counter read as one consistent set.
        /// </summary>
        /// <remarks>
        /// Deliberately not a set of property reads elsewhere. The broker's counter moves on the raw source's
        /// thread while the queue's moves here, so reading them one at a time can report more reports than were
        /// queued 闁?which reads as a lost report and is really two different instants being compared.
        /// </remarks>
        private RawCounters Snapshot()
        {
            // Read as one set, at one instant: the broker's counter moves on the raw source's thread while the
            // queue's moves here, so reading them separately can show more reports than were queued 闁?which reads
            // as a lost report and is really two different instants being compared.
            var reports = _broker.Reports;
            var queued = _queue.Queued;
            var dropped = _queue.Dropped;
            return new RawCounters(reports, queued, dropped, _frames, _applies);
        }

        private void WriteCounters(string label, RawCounters c)
        {
            Console.WriteLine(
                $"INTERACTIVE {label} raw={c.Reports} queued={c.Queued} dropped={c.Dropped} frames={c.Frames} applies={c.Applies}");
            ReportLatency();
            Console.Out.Flush();
        }

        /// <summary>
        /// A mouse report, in physical screen pixels, on the raw source's thread.
        /// </summary>
        /// <remarks>
        /// This method exists only to cross threads. It queues the sample and asks the window to drain, and does no
        /// work that could block the raw source or the rest of its consumers. Every accepted report stays a
        /// separate sample: the queue is not collapsed to the latest position, because a fast sweep must produce
        /// the positions it passed through rather than one merged jump.
        /// </remarks>
        private void OnPointerReport(int screenX, int screenY)
        {
            if (_queue.TryEnqueue(screenX, screenY, Stopwatch.GetTimestamp()))
            {
                _ = PostMessage(_hwnd, NativeWindowHost.WmPointerBatch, 0, nint.Zero);
            }
        }

        /// <summary>
        /// Applies every report waiting, oldest first. Runs on the window's thread, once per posted batch.
        /// </summary>
        public void DrainPointerBatch()
        {
            _batch.Clear();
            var taken = _queue.Drain(_batch);
            if (taken == 0)
            {
                return;
            }

            // Every sample in the batch is applied in order, and a frame is presented once at the end of the
            // batch rather than once per sample: the motion engine's state is the last report's, so composing
            // intermediate frames would be work nobody sees. The follow is still direct and 1:1 闁?nothing is
            // smoothed, and the frame that is shown is composed from the newest report, not an eased guess at it.
            for (var i = 0; i < _batch.Count; i++)
            {
                // Measured before applying, so the number is the report's whole journey: captured on the raw
                // source's thread, queued, posted, and drained. A backlog shows up here as a growing tail,
                // which is the reading that distinguishes "reports are slow" from "the consumer is behind".
                RecordLatency(_batch[i].Timestamp);
                ApplyPointer(_batch[i]);
            }

            Compose(_motion);
            Present();
            _frames++;
        }

        private void ApplyPointer(PointerSample sample)
        {
            var x = _space.ScreenPxToSurfacePx(sample.ScreenX);
            var y = _space.ScreenPxToSurfacePxY(sample.ScreenY);

            var overDock = x >= 0 && x < _surface.Width && y >= 0 && y < _surface.Height;
            var pointerDip = _space.PxToDip(x);
            var down = _buttons.IsLeftDown;

            // The button's edges decide the gesture, and they are applied before the motion so a press and the
            // movement that follows it resolve in the same order the hand made them.
            var pressed = down && !_pointerWasDown;
            var released = !down && _pointerWasDown;

            var hit = _motion.HitTest(
                pointerDip,
                _space.PxToDip(y),
                DockMetrics.IconBoxDip,
                _space.PxToDip(_surface.Height),
                DockMetrics.PlatePaddingYDip);

            if (pressed || released)
            {
                // Every edge, with the phase it arrived in. "Two commits for one drag" means two releases were
                // seen, and the only way to tell a real release from a mis-read button is to print the edges.
                Console.WriteLine(
                    $"INTERACTIVE edge={(pressed ? "down" : "up")} phase={_drag.Phase} slot={hit} offset={_drag.OffsetDip:F1}");
                Console.Out.Flush();
            }

            if (pressed && overDock && hit >= 0)
            {
                _drag.Press(hit, pointerDip);
                _pressedSlot = hit;
                Console.WriteLine($"INTERACTIVE press slot={hit}");
                Console.Out.Flush();
            }
            else if (pressed)
            {
                _pressedSlot = -1;
            }

            if (down || _drag.Phase != DragPhase.Idle)
            {
                _drag.Move(pointerDip);
            }

            if (_drag.IsDragging && _drag.CandidateSlot != _lastCandidate)
            {
                _lastCandidate = _drag.CandidateSlot;
                Console.WriteLine(
                    $"INTERACTIVE candidate slot={_drag.CandidateSlot} from={_drag.DraggedSlot} offset={_drag.OffsetDip:F1}");
                Console.Out.Flush();
            }

            if (released)
            {
                var moved = _drag.Release();
                _lastCandidate = -2;

                if (moved is { } move)
                {
                    // One line per committed reorder, with the order it produced. A drag that commits twice would
                    // print twice, so "exactly one commit per drag" is checkable from this alone.
                    Console.WriteLine(
                        $"INTERACTIVE reorder from={move.From} to={move.To} commits={_order.Commits} order={_order.Describe()}");
                }
                else if (_pressedSlot >= 0)
                {
                    // A press that never travelled far enough is a click, and a click still launches. The target
                    // is the icon the press landed on, not whatever is under the pointer now: a click is decided
                    // when the button goes down, and resolving it at release would launch a different icon — or
                    // nothing — whenever the pointer drifted by a pixel in between, which injected input does
                    // routinely.
                    Launch(_pressedSlot);
                }

                _pressedSlot = -1;
                Console.Out.Flush();
            }

            // While a drag owns the pointer the wave is not drawn: the wave is a statement about the run, and a
            // dragged icon is a statement about one icon being held. The engine is put to rest rather than left
            // frozen, so nothing stale is magnified when the drag ends.
            _motion.Update(_drag.Phase == DragPhase.Idle && overDock ? pointerDip : null);
            _applies++;

            var peak = _motion.PeakScale;
            if (peak > _peakSeen)
            {
                _peakSeen = peak;
                Console.WriteLine($"INTERACTIVE peak={peak:F4} pointer={x}");
                Console.Out.Flush();
            }

            // Hover is reported from the icon the pointer is really over, which while dragging is the carried one.
            var hover = _drag.IsDragging ? _drag.DraggedSlot : hit;
            if (hover != _lastHit)
            {
                _lastHit = hover;
                Console.WriteLine($"INTERACTIVE hover={hover}");
                Console.Out.Flush();
            }

            _pointerWasDown = down;
        }

        private int _frames;
        private int _applies;

        /// <summary>
        /// Capture-to-apply latency in microseconds, one slot per applied report.
        /// </summary>
        /// <remarks>
        /// A fixed buffer rather than a list: this is on the hot path, and a measurement that allocates while it
        /// measures changes the thing it is measuring. Once full it stops recording, which is reported, so a
        /// truncated reading can never be mistaken for a complete one.
        /// </remarks>
        private readonly double[] _latenciesUs = new double[LatencySamples];
        private int _latencyCount;

        private const int LatencySamples = 20000;

        /// <summary>How many of the first reports are treated as startup rather than steady state.</summary>
        private const int WarmupSamples = 3;

        private void RecordLatency(long capturedAt)
        {
            if (_latencyCount >= _latenciesUs.Length)
            {
                return;
            }

            var elapsed = Stopwatch.GetTimestamp() - capturedAt;
            _latenciesUs[_latencyCount++] = elapsed * 1_000_000.0 / Stopwatch.Frequency;
        }

        private void ReportLatency()
        {
            if (_latencyCount == 0)
            {
                Console.WriteLine("INTERACTIVE latencyUs samples=0");
                return;
            }

            var ordered = new double[_latencyCount];
            Array.Copy(_latenciesUs, ordered, _latencyCount);
            Array.Sort(ordered);

            Console.WriteLine(
                $"INTERACTIVE latencyUs samples={_latencyCount} truncated={_latencyCount >= _latenciesUs.Length} "
                + $"p50={Percentile(ordered, 0.50):F1} p95={Percentile(ordered, 0.95):F1} p99={Percentile(ordered, 0.99):F1} max={ordered[^1]:F1}");

            // The same reading with the first reports left out. Reports can only be delivered once the raw
            // source has opened and the window is pumping, and the dock's own startup work 闁?reading five icons
            // from the shell 闁?is still running on this thread at that moment, so the first samples wait behind
            // it. Separating the two is what stops a startup cost from being reported as steady-state latency.
            if (_latencyCount > WarmupSamples)
            {
                var steady = new double[_latencyCount - WarmupSamples];
                Array.Copy(_latenciesUs, WarmupSamples, steady, 0, steady.Length);
                Array.Sort(steady);
                Console.WriteLine(
                    $"INTERACTIVE latencySteadyUs samples={steady.Length} excludedWarmup={WarmupSamples} "
                    + $"p50={Percentile(steady, 0.50):F1} p95={Percentile(steady, 0.95):F1} p99={Percentile(steady, 0.99):F1} max={steady[^1]:F1}");
            }
        }


        /// <summary>
        /// Draws the current frame. Nothing is painted where there is no icon, so the desktop stays visible.
        /// </summary>
        /// <remarks>
        /// The position of an icon is the composition of three separate states, and keeping them separate is what
        /// stops a drag from corrupting the wave or the wave from fighting the drag:
        /// <list type="bullet">
        /// <item>the base slot, which the order decides;</item>
        /// <item>the engine's Nexus translation and lift, which describe the wave;</item>
        /// <item>the drag's own offset, which describes the icon being carried and which the engine never sees.</item>
        /// </list>
        /// The drag offset is added here, at the point of drawing, rather than being handed to the engine.
        /// </remarks>
        private void Compose(DockMotionState motion)
        {
            _surface.Clear();
            var restingBottom = _metrics.RestingBaselinePx;

            _drag.FillCentres(_centresScratch);

            for (var slot = 0; slot < _pins; slot++)
            {
                // The slot is where it is drawn; the order says which target is drawn there.
                var target = _order[slot];

                if (_artwork[target] is not { } art)
                {
                    continue;
                }

                // A carried icon is drawn at its natural size and does not rise: the wave is not running, and a
                // scale from a stopped engine would be a leftover rather than an answer.
                var dragging = _drag.IsDragging && slot == _drag.DraggedSlot;
                var sample = motion.Sample(slot);
                var scale = dragging ? 1.0 : sample.Scale;
                var liftDip = dragging ? 0.0 : sample.Lift;
                var waveDip = dragging ? 0.0 : sample.TranslateX;

                var box = _space.DipToPx(DockMetrics.IconBoxDip * scale);
                if (box <= 0)
                {
                    continue;
                }

                var centreX = _space.DipToPx(_centresScratch[slot] + waveDip);
                var bottom = restingBottom - _space.DipToPx(liftDip);

                // The artwork was read at the largest size this icon can reach, so every frame is a scale-down
                // from a source with more detail than it needs. Scaling up from the resting size would invent the
                // extra detail, which is what made the earlier frames look chunky.
                _surface.DrawIconScaled(art, centreX, bottom, box);
            }
        }

        /// <summary>Where each slot is drawn this frame, in DIP. Reused so composing allocates nothing.</summary>
        private readonly double[] _centresScratch = new double[64];

        private void Present()
        {
            Marshal.Copy(_surface.Pixels, 0, _bits, _surface.Pixels.Length);
            var destination = new Point(_space.WindowOriginX, _space.WindowOriginY);
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
    private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);

    private const uint WmTimer = 0x0113;
    private const nuint CounterTimerId = 1;
    private const uint CounterReportMilliseconds = 2000;

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

