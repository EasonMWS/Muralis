using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The bridge between a plain Win32 thread and the in-box composition engine. Creating a
/// <see cref="Compositor"/> on a thread that has no dispatcher queue fails with E_ACCESSDENIED, so
/// the queue is made first (through CoreMessaging, the documented way for desktop apps), and the
/// composition tree is then put onto the window through <c>ICompositorDesktopInterop</c>, which is
/// a Win32-only interface and therefore not part of the WinRT projection.
/// </summary>
/// <remarks>
/// The dispatcher queue belongs to the thread: one controller is created per shell thread and kept
/// for the life of the process, because releasing it while a compositor from that thread is still
/// in use would take composition down. A compositor belongs to the thread that created it and is
/// created per mount.
/// </remarks>
internal static class CompositionBootstrap
{
    private const uint DqThreadCurrent = 2;
    private const uint DqApartmentComAsta = 1;
    private const uint DqApartmentComNone = 0;

    /// <summary>ICompositorDesktopInterop: CreateDesktopWindowTarget is its first method, after IUnknown.</summary>
    private static readonly Guid CompositorDesktopInteropId = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
    private const int CreateDesktopWindowTargetSlot = 3;

    private static readonly object Gate = new();
    private static nint _dispatcherController;
    private static uint _dispatcherThreadId;

    /// <summary>Creates a compositor for the calling thread, giving it a dispatcher queue first.</summary>
    internal static Compositor CreateCompositor()
    {
        EnsureDispatcherQueue();

        try
        {
            return new Compositor();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The composition engine is not available on this system.", ex);
        }
    }

    /// <summary>Puts a composition tree onto <paramref name="window"/>. The caller owns the target.</summary>
    internal static DesktopWindowTarget CreateTarget(Compositor compositor, nint window)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (window == nint.Zero)
        {
            throw new ArgumentException("A composition target needs a window.", nameof(window));
        }

        var compositorPointer = MarshalInspectable<Compositor>.FromManaged(compositor);
        try
        {
            var result = Marshal.QueryInterface(compositorPointer, in CompositorDesktopInteropId, out var interopPointer);
            if (result < 0)
            {
                throw new InvalidOperationException($"This system's composition engine cannot host a desktop window (0x{result:X8}).");
            }

            try
            {
                result = CreateDesktopWindowTarget(interopPointer, window, out var targetPointer);
                if (result < 0 || targetPointer == nint.Zero)
                {
                    throw new InvalidOperationException($"The composition target for the desktop window could not be created (0x{result:X8}).");
                }

                var target = MarshalInspectable<DesktopWindowTarget>.FromAbi(targetPointer);
                Marshal.Release(targetPointer);
                return target;
            }
            finally
            {
                Marshal.Release(interopPointer);
            }
        }
        finally
        {
            Marshal.Release(compositorPointer);
        }
    }

    private static int CreateDesktopWindowTarget(nint interopPointer, nint window, out nint target)
    {
        var vtable = Marshal.ReadIntPtr(interopPointer);
        var method = Marshal.ReadIntPtr(vtable, CreateDesktopWindowTargetSlot * nint.Size);
        var createTarget = Marshal.GetDelegateForFunctionPointer<CreateWindowTargetDelegate>(method);
        return createTarget(interopPointer, window, 0, out target);
    }

    private static void EnsureDispatcherQueue()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        lock (Gate)
        {
            var threadId = NativeMethods.GetCurrentThreadId();
            if (_dispatcherController != nint.Zero && _dispatcherThreadId == threadId)
            {
                return;
            }

            var options = new DispatcherQueueOptions
            {
                Size = Marshal.SizeOf<DispatcherQueueOptions>(),
                ThreadType = DqThreadCurrent,
                ApartmentType = DqApartmentComAsta,
            };

            var result = CreateDispatcherQueueController(ref options, out var controller);
            if (result < 0)
            {
                // The single-threaded apartment is not always available; composition only needs the
                // queue itself, so the plain option gets a second try.
                options.ApartmentType = DqApartmentComNone;
                result = CreateDispatcherQueueController(ref options, out controller);
            }

            if (result < 0)
            {
                throw new InvalidOperationException($"The thread cannot host composition: its dispatcher queue could not be created (0x{result:X8}).");
            }

            _dispatcherController = controller;
            _dispatcherThreadId = threadId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public uint ThreadType;
        public uint ApartmentType;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateWindowTargetDelegate(nint compositorInterop, nint window, byte topmost, out nint target);

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(ref DispatcherQueueOptions options, out nint controller);
}
