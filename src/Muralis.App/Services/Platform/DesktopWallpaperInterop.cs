using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Muralis.App.Services.Platform;

/// <summary>
/// Minimal, hand-written interop for the two Windows desktop-wallpaper APIs:
/// <c>SystemParametersInfo(SPI_SETDESKWALLPAPER)</c> for applying one image to every
/// display, and the <c>IDesktopWallpaper</c> COM interface (Windows 8+) for
/// per-monitor wallpapers. Only the leading members of the interface are declared,
/// in vtable order; do not add members out of order.
/// </summary>
internal static class DesktopWallpaperInterop
{
    internal const uint SpiSetDeskWallpaper = 0x0014;
    internal const uint SpifUpdateIniFile = 0x01;
    internal const uint SpifSendChange = 0x02;

    private static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    /// <summary>Applies the image to all displays, persists it, and broadcasts the change.</summary>
    internal static void SetWallpaperForAllMonitors(string imagePath)
    {
        if (!SystemParametersInfo(SpiSetDeskWallpaper, 0, imagePath, SpifUpdateIniFile | SpifSendChange))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "SystemParametersInfo(SPI_SETDESKWALLPAPER) failed.");
        }
    }

    internal static IDesktopWallpaper CreateDesktopWallpaper()
    {
        var coclass = Type.GetTypeFromCLSID(ClsidDesktopWallpaper, throwOnError: true)!;
        return (IDesktopWallpaper)Activator.CreateInstance(coclass)!;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    /// <summary>Matches DESKTOP_WALLPAPER_POSITION in shobjidl_core.h.</summary>
    internal enum DesktopWallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);

        void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);

        void GetMonitorDevicePathCount(out uint count);

        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeRect displayRect);

        void SetBackgroundColor(uint color);

        void GetBackgroundColor(out uint color);

        void SetPosition(DesktopWallpaperPosition position);

        void GetPosition(out DesktopWallpaperPosition position);
    }
}
