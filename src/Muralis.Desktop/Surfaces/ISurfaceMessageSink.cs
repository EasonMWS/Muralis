namespace Muralis.Desktop.Surfaces;

/// <summary>
/// Content that handles the messages of its own window. The surface host still owns the window —
/// it creates, positions and destroys it — but an interactive content needs the raw input the
/// window receives, so the host forwards everything that is not window lifecycle to the sink the
/// mounted content provides. Messages arrive on the shell thread, in dispatch order, and only
/// while the surface is mounted.
/// </summary>
internal interface ISurfaceMessageSink
{
    /// <summary>
    /// Handles one window message. Returns true when the message was consumed; the value handed
    /// back to Windows is then the one in <paramref name="result"/>.
    /// </summary>
    bool OnWindowMessage(nint window, uint message, nint wParam, nint lParam, out nint result);
}
