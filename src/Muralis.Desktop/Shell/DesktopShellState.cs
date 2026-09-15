namespace Muralis.Desktop.Shell;

/// <summary>Lifecycle of the process-wide desktop shell.</summary>
public enum DesktopShellState
{
    /// <summary>The shell thread is not running; nothing is mounted.</summary>
    Stopped,

    /// <summary>The shell thread is coming up and the desktop layer is being located.</summary>
    Starting,

    /// <summary>The shell is up and owns the desktop layer for as long as it runs.</summary>
    Running,

    /// <summary>The shell is shutting down and releasing everything it mounted.</summary>
    Stopping,
}
