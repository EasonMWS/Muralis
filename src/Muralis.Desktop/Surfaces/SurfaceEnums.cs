namespace Muralis.Desktop.Surfaces;

/// <summary>
/// What a surface provides. The kind decides which desktop layer the surface goes to, how many of
/// them a display may have, and whether it may share a window with other content. Only kinds that
/// are actually implemented get added — the rendering technology behind non-backdrop kinds is
/// deliberately not locked down yet.
/// </summary>
public enum SurfaceKind
{
    /// <summary>A full-display desktop background, behind the icons. At most one per display.</summary>
    Backdrop,
}

/// <summary>How much input a surface needs. Declared, not inferred: the wallpaper layer cannot take input at all.</summary>
public enum SurfaceInteraction
{
    None,
    Pointer,
    PointerAndKeyboard,
}

/// <summary>When a surface may take activation/focus. Default is <see cref="Never"/>; interaction must be explicit.</summary>
public enum SurfaceActivation
{
    Never,
    OnClick,
    Always,
}

/// <summary>Which layer of the desktop a surface lives in.</summary>
public enum SurfaceLayer
{
    /// <summary>Behind the desktop icons; not reachable by the mouse.</summary>
    WallpaperLayer,

    /// <summary>Above the icons — reserved for future interactive content.</summary>
    DesktopInteractiveLayer,
}

/// <summary>Where one mount is in its lifecycle (see the shell's mount state machine).</summary>
public enum SurfaceState
{
    /// <summary>Not mounted anywhere.</summary>
    Detached,

    /// <summary>Mounted and showing on its display.</summary>
    Mounted,

    /// <summary>The host it was mounted on disappeared (Explorer restart, worker destroyed); the shell re-mounts it.</summary>
    Orphaned,
}
