using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Desktop.Icons;
using Muralis.Desktop.Interop;
using Xunit;

namespace Muralis.Desktop.Tests.Icons;

/// <summary>
/// The one part of the icon path that pure code cannot prove: whether the composition engine really
/// takes the pixels, and what fifty of them cost. A real icon is read from the shell, carried by a
/// swap chain and drawn by the engine — only the engine can say whether it accepts the chain, and
/// if it refused, every real icon would silently stay a glyph.
/// </summary>
/// <remarks>Set <c>MURALIS_ICON_LIVE=1</c> to run; the steps land in <c>icon-live.log</c>.</remarks>
public sealed class IconSurfaceLiveTests
{
    [Fact]
    public void TryCreate_PutsARealIconOnASurfaceTheCompositorDraws()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_ICON_LIVE") is null)
        {
            return;
        }

        var iconPath = IconPath();

        RunOnStaThread(() =>
        {
            var bitmap = ShellIconReader.Read(iconPath, 96);
            Assert.NotNull(bitmap);
            Probe($"read: {bitmap!.Width}x{bitmap.Height}, {bitmap.ByteCount} bytes");

            var device = IconSurfaceDevice.TryCreate(NullLogger.Instance);
            Assert.NotNull(device);
            try
            {
                Probe("device");
                var compositor = CompositionBootstrap.CreateCompositor();
                try
                {
                    var icon = IconSurface.TryCreate(compositor, device!, bitmap, NullLogger.Instance);
                    Assert.NotNull(icon);

                    try
                    {
                        // A visual is what the canvas draws with, so the brush has to survive being
                        // handed to the compositor that just produced it.
                        var visual = compositor.CreateSpriteVisual();
                        visual.Size = new System.Numerics.Vector2(96, 96);
                        visual.Brush = icon!.Brush;
                        Probe("brush");
                    }
                    finally
                    {
                        icon!.Dispose();
                        Probe("surface released");
                    }
                }
                finally
                {
                    compositor.Dispose();
                    Probe("compositor disposed");
                }
            }
            finally
            {
                device!.Dispose();
                Probe("device disposed");
            }
        });
    }

    /// <summary>
    /// Fifty items is what the phase is asked to stay smooth with, and every icon means one more
    /// swap chain: the number this logs is what a fifty item layout pays before it first draws.
    /// </summary>
    [Fact]
    public void TryCreate_FiftyIcons_ComeBackInAUsefulTime()
    {
        if (Environment.GetEnvironmentVariable("MURALIS_ICON_LIVE") is null)
        {
            return;
        }

        var iconPath = IconPath();

        RunOnStaThread(() =>
        {
            var bitmap = ShellIconReader.Read(iconPath, 96);
            Assert.NotNull(bitmap);

            var device = IconSurfaceDevice.TryCreate(NullLogger.Instance);
            Assert.NotNull(device);
            try
            {
                var compositor = CompositionBootstrap.CreateCompositor();
                try
                {
                    var icons = new List<IconSurface>();
                    var watch = Stopwatch.StartNew();
                    for (var i = 0; i < 50; i++)
                    {
                        var icon = IconSurface.TryCreate(compositor, device!, bitmap!, NullLogger.Instance);
                        Assert.NotNull(icon);
                        icons.Add(icon!);
                    }

                    watch.Stop();
                    Probe($"fifty surfaces: {watch.ElapsedMilliseconds} ms ({watch.ElapsedMilliseconds / 50.0:0.0} ms each)");

                    foreach (var icon in icons)
                    {
                        icon.Dispose();
                    }

                    Probe("fifty surfaces released");
                }
                finally
                {
                    compositor.Dispose();
                }
            }
            finally
            {
                device!.Dispose();
            }
        });
    }

    private static string IconPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Assert.True(File.Exists(path), $"the live check needs a real icon source, but {path} is gone");
        return path;
    }

    private static void Probe(string step) =>
        File.AppendAllText(
            Path.Combine(AppContext.BaseDirectory, "icon-live.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {step}{Environment.NewLine}");

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Icon surface live check",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "the icon surface live check did not finish in time");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
