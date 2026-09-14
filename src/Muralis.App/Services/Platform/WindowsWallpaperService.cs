using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.Services.Platform;

/// <summary>
/// Applies wallpapers on Windows:
/// <list type="bullet">
/// <item>all displays → wallpaper style in the registry + <c>SPI_SETDESKWALLPAPER</c>;</item>
/// <item>a single display → the <c>IDesktopWallpaper</c> COM API, with a graceful fall back to all displays.</item>
/// </list>
/// </summary>
public sealed class WindowsWallpaperService : IWallpaperService
{
    private const string DesktopRegistryPath = @"Control Panel\Desktop";

    private readonly IImageFormatService _imageFormatService;
    private readonly ILogger<WindowsWallpaperService> _logger;

    public WindowsWallpaperService(IImageFormatService imageFormatService, ILogger<WindowsWallpaperService> logger)
    {
        _imageFormatService = imageFormatService;
        _logger = logger;
    }

    public Task<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<MonitorInfo>>(GetMonitorsCore, cancellationToken);

    public async Task<string> SetWallpaperAsync(
        string imagePath,
        WallpaperFitMode fitMode,
        string? monitorId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("The wallpaper image was not found on disk.", imagePath);
        }

        var appliedPath = await _imageFormatService
            .EnsureSupportedFormatAsync(imagePath, cancellationToken)
            .ConfigureAwait(false);

        await Task.Run(
            () =>
            {
                if (string.IsNullOrEmpty(monitorId))
                {
                    ApplyToAllMonitors(appliedPath, fitMode);
                }
                else
                {
                    ApplyToMonitor(monitorId, appliedPath, fitMode);
                }
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Wallpaper applied (mode: {Mode}, monitor: {Monitor}, file: {Path})",
            fitMode,
            string.IsNullOrEmpty(monitorId) ? "all" : monitorId,
            appliedPath);

        return appliedPath;
    }

    private List<MonitorInfo> GetMonitorsCore()
    {
        try
        {
            var desktopWallpaper = DesktopWallpaperInterop.CreateDesktopWallpaper();
            desktopWallpaper.GetMonitorDevicePathCount(out var count);

            var monitors = new List<MonitorInfo>((int)count);
            for (uint index = 0; index < count; index++)
            {
                desktopWallpaper.GetMonitorDevicePathAt(index, out var devicePath);
                desktopWallpaper.GetMonitorRECT(devicePath, out var rect);

                monitors.Add(new MonitorInfo
                {
                    Id = devicePath,
                    DisplayName = $"Display {index + 1}",
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height,
                    IsPrimary = rect is { Left: 0, Top: 0 },
                });
            }

            // Primary display first, then left-to-right.
            return monitors
                .OrderByDescending(monitor => monitor.IsPrimary)
                .ThenBy(monitor => monitor.X)
                .ToList();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or MissingMethodException)
        {
            _logger.LogError(ex, "Could not enumerate displays through IDesktopWallpaper");
            return [];
        }
    }

    private void ApplyToAllMonitors(string path, WallpaperFitMode mode)
    {
        WriteRegistryStyle(mode);
        DesktopWallpaperInterop.SetWallpaperForAllMonitors(path);
    }

    private void ApplyToMonitor(string monitorId, string path, WallpaperFitMode mode)
    {
        try
        {
            var desktopWallpaper = DesktopWallpaperInterop.CreateDesktopWallpaper();
            desktopWallpaper.SetPosition(MapPosition(mode));
            desktopWallpaper.SetWallpaper(monitorId, path);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException or MissingMethodException)
        {
            _logger.LogWarning(ex, "Per-monitor wallpaper failed; applying to all displays instead");
            ApplyToAllMonitors(path, mode);
        }
    }

    /// <summary>
    /// SPI_SETDESKWALLPAPER applies whatever style is stored in the registry,
    /// so the style must be written first.
    /// </summary>
    private static void WriteRegistryStyle(WallpaperFitMode mode)
    {
        var (style, tile) = mode switch
        {
            WallpaperFitMode.Center => ("0", "0"),
            WallpaperFitMode.Tile => ("0", "1"),
            WallpaperFitMode.Stretch => ("2", "0"),
            WallpaperFitMode.Fit => ("6", "0"),
            WallpaperFitMode.Fill => ("10", "0"),
            WallpaperFitMode.Span => ("22", "0"),
            _ => ("10", "0"),
        };

        using var key = Registry.CurrentUser.OpenSubKey(DesktopRegistryPath, writable: true)
            ?? throw new InvalidOperationException($@"The registry key HKCU\{DesktopRegistryPath} is not accessible.");

        key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
        key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
    }

    private static DesktopWallpaperInterop.DesktopWallpaperPosition MapPosition(WallpaperFitMode mode) => mode switch
    {
        WallpaperFitMode.Center => DesktopWallpaperInterop.DesktopWallpaperPosition.Center,
        WallpaperFitMode.Tile => DesktopWallpaperInterop.DesktopWallpaperPosition.Tile,
        WallpaperFitMode.Stretch => DesktopWallpaperInterop.DesktopWallpaperPosition.Stretch,
        WallpaperFitMode.Fit => DesktopWallpaperInterop.DesktopWallpaperPosition.Fit,
        WallpaperFitMode.Span => DesktopWallpaperInterop.DesktopWallpaperPosition.Span,
        _ => DesktopWallpaperInterop.DesktopWallpaperPosition.Fill,
    };
}
