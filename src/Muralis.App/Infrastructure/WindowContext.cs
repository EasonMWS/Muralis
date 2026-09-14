using Microsoft.UI.Xaml;

namespace Muralis.App.Infrastructure;

/// <summary>
/// Gives services access to the main window (dialogs, pickers) without
/// introducing a circular dependency on <see cref="MainWindow"/> itself.
/// </summary>
public sealed class WindowContext
{
    public Window? MainWindow { get; set; }

    public nint Handle => MainWindow is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);

    public XamlRoot? XamlRoot => MainWindow?.Content?.XamlRoot;

    public bool IsReady => MainWindow is not null;
}
