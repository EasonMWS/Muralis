namespace Muralis.Desktop.Input;

/// <summary>
/// What the pointer is over, from the desktop's point of view. Only <see cref="Foreign"/> means the
/// user is working somewhere else — a browser, the taskbar, any ordinary window — and the desktop
/// must stay still. Over the desktop itself, or over a surface of ours, the pointer is ours to
/// follow.
/// </summary>
public enum DesktopPointerContext
{
    /// <summary>An ordinary window of another application (or the taskbar): nothing on the desktop may react.</summary>
    Foreign,

    /// <summary>The desktop layer itself: the icon host, the wallpaper worker or the Progman window.</summary>
    Desktop,

    /// <summary>A desktop surface window of this process — the canvas today.</summary>
    Surface,
}
