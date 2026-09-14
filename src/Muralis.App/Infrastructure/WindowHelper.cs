using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Muralis.App.Infrastructure;

internal static class WindowHelper
{
    private const int DefaultWidth = 1360;
    private const int DefaultHeight = 860;

    /// <summary>
    /// Sizes and centers the window on first launch, taking the monitor DPI into account.
    /// </summary>
    public static void ConfigureInitialPlacement(Window window)
    {
        var appWindow = window.AppWindow;
        if (appWindow is null)
        {
            return;
        }

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
        var width = (int)Math.Round(DefaultWidth * scale);
        var height = (int)Math.Round(DefaultHeight * scale);

        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);

        appWindow.ResizeClient(new SizeInt32(width, height));
        appWindow.Move(new PointInt32(
            workArea.X + ((workArea.Width - width) / 2),
            workArea.Y + ((workArea.Height - height) / 2)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
