using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Muralis.Core.Desktop.Takeover;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Takeover;

/// <summary>
/// The shell's own desktop view, opened for the length of one operation: the documented chain from the
/// ShellWindows collection down to the view that draws the desktop, plus the icon list window that the
/// same desktop has. Everything it can do is a read, or the one reversible flag write; closing it
/// releases every interface it took.
/// </summary>
/// <remarks>
/// <para>
/// It is opened and closed per operation rather than held: the shell rebuilds the whole desktop view
/// when Explorer restarts, and a pointer kept from before such a restart is a pointer into a dead
/// object. Re-opening costs a handful of calls and removes that entire class of problem.
/// </para>
/// <para>
/// Opening includes a self-check. The interfaces below are declared by vtable slot, so a shell that
/// ordered them differently would be talking to the wrong methods; reading the view mode first, and
/// requiring a value the shell documents, is what turns that into a reported failure instead of a
/// silent write to the wrong place.
/// </para>
/// </remarks>
internal sealed class DesktopShellView : IDisposable
{
    /// <summary>The shell's own folder view modes: icons, small icons, list, details, thumbnails, tiles.</summary>
    private const uint LowestViewMode = 1;
    private const uint HighestViewMode = 6;

    /// <summary><c>SVGIO_ALLVIEW</c>: count every item the view holds, not one selection.</summary>
    private const uint CountAllViewItems = 0x2;

    private readonly ShellViewInterfaces.IShellFolderView _view;
    private readonly ShellViewInterfaces.IShellFolderView2? _view2;
    private readonly object?[] _acquired;

    private DesktopShellView(ShellViewInterfaces.IShellFolderView view, ShellViewInterfaces.IShellFolderView2? view2, object?[] acquired, uint viewMode, int itemCount)
    {
        _view = view;
        _view2 = view2;
        _acquired = acquired;
        ViewMode = viewMode;
        ItemCount = itemCount;
        IconList = DesktopWorkerWindow.FindIconList();
    }

    /// <summary>Which of the shell's view modes the desktop view is in; read as the self-check.</summary>
    internal uint ViewMode { get; }

    /// <summary>How many items the view holds, whether or not it draws them.</summary>
    internal int ItemCount { get; }

    /// <summary>The window that draws the icons, or <c>nint.Zero</c> when the shell has none.</summary>
    internal nint IconList { get; }

    /// <summary>Whether the flags interface is there, which is what the documented way in needs.</summary>
    internal bool CanReadFlags => _view2 is not null;

    /// <summary>
    /// Opens the desktop's view, or reports why it could not. Must be called on the thread that owns
    /// the apartment — the view is handed back inside one.
    /// </summary>
    internal static DesktopShellView? Open(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var taken = new List<object?>(4);
        object?[]? handedOver = null;

        try
        {
            var created = NativeMethods.CoCreateInstance(
                ShellViewInterfaces.ShellWindowsClassId,
                null,
                NativeMethods.ClsCtxAll,
                ShellViewInterfaces.ShellWindowsInterfaceId,
                out var shellWindows);

            if (created != 0 || shellWindows is null)
            {
                logger.LogWarning("The shell's window collection could not be created ({Result})", created);
                return null;
            }

            taken.Add(shellWindows);

            if (shellWindows is not ShellViewInterfaces.IShellWindows windows)
            {
                logger.LogWarning("The shell's window collection does not offer the documented interface");
                return null;
            }

            var location = ShellViewInterfaces.ShellLocation.Of(ShellViewInterfaces.DesktopFolderOrdinal);
            var locationRoot = ShellViewInterfaces.ShellLocation.Empty();
            var found = windows.FindWindowSW(
                ref location,
                ref locationRoot,
                ShellViewInterfaces.ShellWindowClassDesktop,
                out _,
                ShellViewInterfaces.ShellWindowFindNeedDispatch,
                out var browser);

            if (found != 0 || browser is null)
            {
                logger.LogWarning("The desktop window could not be asked for ({Result})", found);
                return null;
            }

            taken.Add(browser);

            if (browser is not ShellViewInterfaces.IShellServiceProvider services)
            {
                logger.LogWarning("The desktop window does not offer the documented service lookup");
                return null;
            }

            var queried = services.QueryService(
                ShellViewInterfaces.TopLevelBrowserServiceId,
                ShellViewInterfaces.ShellBrowserInterfaceId,
                out var shellBrowser);

            if (queried != 0 || shellBrowser is null)
            {
                logger.LogWarning("The desktop's browser could not be reached ({Result})", queried);
                return null;
            }

            taken.Add(shellBrowser);

            if (shellBrowser is not ShellViewInterfaces.IShellBrowser browserView
                || browserView.QueryActiveShellView(out var activeView) != 0
                || activeView is null)
            {
                logger.LogWarning("The desktop's view could not be reached");
                return null;
            }

            taken.Add(activeView);

            if (activeView is not ShellViewInterfaces.IShellFolderView view)
            {
                logger.LogWarning("The desktop's view does not offer the documented folder view interface");
                return null;
            }

            // The self-check: this slot is the first method of the interface, so it is the one that
            // says whether the declaration and the shell agree.
            var modeResult = view.GetCurrentViewMode(out var viewMode);
            if (modeResult != 0 || viewMode is < LowestViewMode or > HighestViewMode)
            {
                logger.LogWarning(
                    "The desktop's view did not answer as the documentation says it would (result {Result}, mode {Mode})",
                    modeResult,
                    viewMode);
                return null;
            }

            var countResult = view.ItemCount(CountAllViewItems, out var itemCount);
            if (countResult != 0 || itemCount < 0)
            {
                logger.LogWarning("The desktop's view could not count its items ({Result})", countResult);
                itemCount = 0;
            }

            // One more slot is read before anything is written, because the whole declaration rests on
            // the slots being where the documentation puts them. Auto arrange is the user's own setting
            // and is only ever read, so it is the safe one to prove the layout with: a shell that ordered
            // the interface differently fails here, in the log, rather than at the first write.
            logger.LogInformation(
                "The desktop's view answered with mode {Mode}, {Count} items and auto arrange {Arrange}",
                viewMode,
                itemCount,
                view.GetAutoArrange());

            // IFolderView2 is what carries the folder flags. A shell without it still has a desktop,
            // so this is not fatal: the ladder simply starts at the rung below.
            var view2 = activeView as ShellViewInterfaces.IShellFolderView2;

            handedOver = [.. taken];
            return new DesktopShellView(view, view2, handedOver, viewMode, itemCount);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or NotSupportedException)
        {
            logger.LogWarning(ex, "The desktop's view could not be opened");
            return null;
        }
        finally
        {
            // An open that did not finish hands back everything it took; one that did hands it to the
            // view, which releases it when the operation is over.
            if (handedOver is null)
            {
                foreach (var acquired in taken)
                {
                    Release(acquired);
                }
            }
        }
    }

    /// <summary>
    /// The view's whole flag word, or null when it could not be read. It carries the user's own
    /// settings — auto arrange, snap to grid, the view mode — as well as whether the icons are drawn,
    /// which is why it is kept whole rather than as one bit.
    /// </summary>
    internal uint? FolderFlags()
    {
        if (_view2 is null)
        {
            return null;
        }

        return _view2.GetCurrentFolderFlags(out var flags) == 0 ? flags : null;
    }

    /// <summary>
    /// Changes the flags named by <paramref name="mask"/>, one bit group at a time, and leaves the rest
    /// of the word alone. This is the documented way to stop the shell drawing the desktop icons.
    /// </summary>
    internal int SetFolderFlags(uint mask, uint flags) => _view2?.SetCurrentFolderFlags(mask, flags) ?? -1;

    /// <summary>
    /// Whether the icons are on screen: the flag word says so and, when the shell has an icon list at
    /// all, that list is visible. The two are read together because either one alone can be stale.
    /// </summary>
    internal bool IconsAreDrawn()
    {
        if (FolderFlags() is { } flags && (flags & ShellViewInterfaces.FolderFlagNoIcons) != 0)
        {
            return false;
        }

        return IconList == nint.Zero || NativeMethods.IsWindowVisible(IconList);
    }

    /// <summary>
    /// What the desktop looks like right now, as something a give-back can put back: the whole flag
    /// word, whether the icons are drawn, and whether there was an icon window to speak of.
    /// <see cref="NativeDesktopVisualState.Observed"/> is false only when the shell answered nothing at
    /// all — neither the flags nor the icon window — which is what stops a recovery from pretending it
    /// knows the desktop.
    /// </summary>
    internal NativeDesktopVisualState VisualState()
    {
        var flags = FolderFlags();

        return new NativeDesktopVisualState
        {
            Observed = flags is not null || IconList != nint.Zero,
            OriginalIconsVisible = IconsAreDrawn(),
            OriginalFolderFlags = flags ?? 0,
            HadIconWindow = IconList != nint.Zero,
        };
    }

    /// <summary>Hides the icon list window. The last resort, and reported as false when it did not take.</summary>
    internal bool HideIconList()
    {
        if (IconList == nint.Zero)
        {
            return false;
        }

        NativeMethods.ShowWindow(IconList, NativeMethods.SwHide);
        return !NativeMethods.IsWindowVisible(IconList);
    }

    /// <summary>Shows the icon list window again.</summary>
    internal bool ShowIconList()
    {
        if (IconList == nint.Zero)
        {
            return false;
        }

        NativeMethods.ShowWindow(IconList, NativeMethods.SwShow);
        return NativeMethods.IsWindowVisible(IconList);
    }

    public void Dispose()
    {
        foreach (var acquired in _acquired)
        {
            Release(acquired);
        }

        Array.Clear(_acquired);
    }

    /// <summary>Gives one acquired interface back. The last release of a shell object closes it.</summary>
    private static void Release(object? acquired)
    {
        if (acquired is not null && Marshal.IsComObject(acquired))
        {
            Marshal.ReleaseComObject(acquired);
        }
    }
}
