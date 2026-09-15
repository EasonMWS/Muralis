using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Muralis.App.Infrastructure;
using Muralis.App.Services.Platform;

namespace Muralis.App.Services;

/// <summary>
/// System tray presence built directly on Shell_NotifyIcon: left click restores the
/// window, right click opens a native context menu (show / next wallpaper / settings / exit).
/// </summary>
public sealed class TrayService : IDisposable
{
    private const uint MenuShow = 1;
    private const uint MenuNextWallpaper = 2;
    private const uint MenuSettings = 3;
    private const uint MenuExit = 4;

    private readonly WindowContext _windowContext;
    private readonly INavigationService _navigation;
    private readonly RotationService _rotation;
    private readonly ILocalizationService _localization;
    private readonly ILogger<TrayService> _logger;
    private readonly TrayInterop.WindowProc _windowProc;

    private nint _messageWindow;
    private nint _iconHandle;
    private string? _className;
    private bool _disposed;

    public TrayService(
        WindowContext windowContext,
        INavigationService navigation,
        RotationService rotation,
        ILocalizationService localization,
        ShellLifecycleWatcher shellWatcher,
        ILogger<TrayService> logger)
    {
        _windowContext = windowContext;
        _navigation = navigation;
        _rotation = rotation;
        _localization = localization;
        _logger = logger;
        _windowProc = OnWindowMessage;

        _localization.LanguageChanged += (_, _) => UpdateTooltip();

        // Explorer restarts drop every notification icon; the shell announcement is the
        // documented signal to put ours back.
        shellWatcher.ShellRestarted += (_, _) => ReAddTrayIcon();

        TryCreateTrayIcon();
    }

    private void TryCreateTrayIcon()
    {
        try
        {
            var instance = GetModuleHandle(null);
            _className = "MuralisTrayHost_" + Guid.NewGuid().ToString("N");

            var windowClass = new TrayInterop.WindowClass
            {
                WindowProc = _windowProc,
                Instance = instance,
                ClassName = _className,
            };

            if (TrayInterop.RegisterClassW(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    $"RegisterClass failed ({Marshal.GetLastWin32Error()}).");
            }

            _messageWindow = TrayInterop.CreateWindowExW(
                0, _className, "Muralis tray", 0, 0, 0, 0, 0,
                TrayInterop.HwndMessage, 0, instance, 0);

            if (_messageWindow == 0)
            {
                throw new InvalidOperationException(
                    $"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                _iconHandle = TrayInterop.LoadImage(
                    0, iconPath, TrayInterop.ImageIcon, 0, 0,
                    TrayInterop.LrLoadFromFile | TrayInterop.LrDefaultSize);
            }

            var data = CreateIconData();

            if (!TrayInterop.Shell_NotifyIcon(TrayInterop.NimAdd, ref data))
            {
                throw new InvalidOperationException(
                    $"Shell_NotifyIcon failed ({Marshal.GetLastWin32Error()}).");
            }

            _logger.LogInformation("Tray icon created");
        }
        catch (Exception ex)
        {
            // The tray is a convenience; the app keeps working without it.
            _logger.LogError(ex, "Could not create the tray icon");
            Cleanup();
        }
    }

    /// <summary>Puts the icon back into the notification area after Explorer recreated it.</summary>
    private void ReAddTrayIcon()
    {
        if (_disposed || _messageWindow == 0)
        {
            return;
        }

        try
        {
            var data = CreateIconData();
            if (TrayInterop.Shell_NotifyIcon(TrayInterop.NimAdd, ref data))
            {
                _logger.LogInformation("Tray icon re-added after the shell restart");
            }
            else
            {
                _logger.LogWarning("The tray icon could not be re-added ({Error})", Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The tray icon could not be re-added");
        }
    }

    private TrayInterop.NotifyIconData CreateIconData() => new()
    {
        Size = Marshal.SizeOf<TrayInterop.NotifyIconData>(),
        WindowHandle = _messageWindow,
        Id = 1,
        Flags = TrayInterop.NifMessage | TrayInterop.NifIcon | TrayInterop.NifTip,
        CallbackMessage = TrayInterop.WmTrayCallback,
        IconHandle = _iconHandle,
        Tip = _localization.Get("Tray_Tooltip"),
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    public void ShowMainWindow()
    {
        if (_windowContext.MainWindow is not { } window)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.AppWindow.Show();
            window.Activate();
        });
    }

    public void HideMainWindow()
    {
        if (_windowContext.MainWindow is { } window)
        {
            window.AppWindow.Hide();
        }
    }

    private nint OnWindowMessage(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (message == TrayInterop.WmTrayCallback && !_disposed)
        {
            var mouseMessage = (int)(lParam & 0xFFFF);
            switch (mouseMessage)
            {
                case TrayInterop.WmLeftButtonUp:
                    ShowMainWindow();
                    break;
                case TrayInterop.WmRightButtonUp:
                    ShowContextMenu();
                    break;
            }
        }
        else if (message == TrayInterop.WmDestroy)
        {
            TrayInterop.PostMessageW(hWnd, TrayInterop.WmNull, 0, 0);
            return 0;
        }

        return TrayInterop.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = TrayInterop.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            TrayInterop.AppendMenuW(menu, TrayInterop.MfString, (nint)MenuShow, _localization.Get("Tray_Show"));
            TrayInterop.AppendMenuW(menu, TrayInterop.MfString, (nint)MenuNextWallpaper, _localization.Get("Tray_NextWallpaper"));
            TrayInterop.AppendMenuW(menu, TrayInterop.MfString, (nint)MenuSettings, _localization.Get("Tray_Settings"));
            TrayInterop.AppendMenuW(menu, TrayInterop.MfSeparator, 0, string.Empty);
            TrayInterop.AppendMenuW(menu, TrayInterop.MfString, (nint)MenuExit, _localization.Get("Tray_Exit"));

            // Required so the menu closes correctly when clicking elsewhere.
            TrayInterop.SetForegroundWindow(_messageWindow);
            TrayInterop.GetCursorPos(out var cursor);

            var command = TrayInterop.TrackPopupMenu(
                menu,
                TrayInterop.TpmReturnCmd | TrayInterop.TpmRightButton,
                cursor.X,
                cursor.Y,
                0,
                _messageWindow,
                0);

            TrayInterop.PostMessageW(_messageWindow, TrayInterop.WmNull, 0, 0);

            switch ((uint)command)
            {
                case MenuShow:
                    ShowMainWindow();
                    break;
                case MenuNextWallpaper:
                    _ = _rotation.ApplyNextAsync();
                    break;
                case MenuSettings:
                    ShowMainWindow();
                    _navigation.NavigateTo(Routes.Settings);
                    break;
                case MenuExit:
                    (_windowContext.MainWindow as MainWindow)?.ExitApplication();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not show the tray menu");
        }
        finally
        {
            TrayInterop.DestroyMenu(menu);
        }
    }

    /// <summary>Refreshes the hover tooltip after a display-language change.</summary>
    private void UpdateTooltip()
    {
        if (_disposed || _messageWindow == 0)
        {
            return;
        }

        try
        {
            var data = new TrayInterop.NotifyIconData
            {
                Size = Marshal.SizeOf<TrayInterop.NotifyIconData>(),
                WindowHandle = _messageWindow,
                Id = 1,
                Flags = TrayInterop.NifTip,
                Tip = _localization.Get("Tray_Tooltip"),
                Info = string.Empty,
                InfoTitle = string.Empty,
            };

            TrayInterop.Shell_NotifyIcon(TrayInterop.NimModify, ref data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the tray tooltip");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    private void Cleanup()
    {
        if (_messageWindow != 0)
        {
            var data = new TrayInterop.NotifyIconData
            {
                Size = Marshal.SizeOf<TrayInterop.NotifyIconData>(),
                WindowHandle = _messageWindow,
                Id = 1,
            };
            TrayInterop.Shell_NotifyIcon(TrayInterop.NimDelete, ref data);
        }

        if (_iconHandle != 0)
        {
            TrayInterop.DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }

        if (_messageWindow != 0)
        {
            TrayInterop.DestroyWindow(_messageWindow);
            _messageWindow = 0;
        }

        if (_className is not null)
        {
            TrayInterop.UnregisterClassW(_className, GetModuleHandle(null));
            _className = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cleanup();
    }
}
