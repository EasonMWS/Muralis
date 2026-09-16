using Muralis.Desktop.Interop;
using Muralis.Desktop.Surfaces;

namespace Muralis.Desktop.Input;

/// <summary>
/// Turns the window under the pointer into what the desktop is dealing with. The walk goes up the
/// parent chain: a surface window of ours means us (the canvas is a child of the icon host, so it
/// has to be recognised before anything else on the chain), a shell desktop class means the
/// desktop, and everything else — other applications, the taskbar — is foreign and left alone.
/// </summary>
internal static class PointerWindowClassifier
{
    /// <summary>The chain is at most a handful of windows deep; the bound only guards against loops.</summary>
    private const int MaxDepth = 32;

    internal static DesktopPointerContext Classify(nint underPointer, IPointerWindowProbe probe)
    {
        var window = underPointer;
        for (var depth = 0; depth < MaxDepth && window != nint.Zero; depth++)
        {
            var className = probe.ClassNameOf(window);
            if (string.Equals(className, Win32SurfaceHost.WindowClassName, StringComparison.Ordinal))
            {
                return DesktopPointerContext.Surface;
            }

            if (DesktopWorkerWindow.IsDesktopLayerClass(className))
            {
                return DesktopPointerContext.Desktop;
            }

            var parent = probe.ParentOf(window);
            if (parent == nint.Zero || parent == window || parent == probe.DesktopWindow)
            {
                break;
            }

            window = parent;
        }

        return DesktopPointerContext.Foreign;
    }
}
