using System.Runtime.InteropServices;
using Muralis.Core.Models;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Monitors;

/// <summary>
/// Reads the display the desktop layer mounts on. Deliberately minimal: one display, no EDID and no
/// identity derivation, so surfaces can name the display they are bound to before the full
/// enumeration exists. The placeholder id is never persisted and is replaced together with the
/// Win32 display enumeration in Phase 2.
/// </summary>
internal static class PrimaryDisplayProbe
{
    /// <summary>Placeholder stable id; the real id comes from the EDID-derived identity.</summary>
    internal const string PlaceholderStableId = "primary";

    internal static Monitor? Read()
    {
        // The same query the host used to position its window: the display nearest the origin that
        // the shell marks as primary.
        var monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MonitorDefaultToPrimary);

        var info = new NativeMethods.MonitorInfoEx { Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        var bounds = new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height);
        var workArea = new PixelRect(info.Work.Left, info.Work.Top, info.Work.Width, info.Work.Height);
        var deviceName = string.IsNullOrWhiteSpace(info.DeviceName) ? PlaceholderStableId : info.DeviceName;
        var (dpi, scale) = ReadDpi(monitor);

        var identity = new MonitorIdentity(
            PlaceholderStableId,
            Edid: null,
            IdentityConfidence.SignatureFallback,
            LastKnownFriendlyName: deviceName);

        var runtime = new MonitorRuntimeInfo(
            DevicePath: deviceName,
            DeviceName: deviceName,
            FriendlyName: deviceName,
            IsPrimary: true,
            OrderIndex: 0,
            bounds,
            workArea,
            dpi,
            scale,
            bounds.Width < bounds.Height ? MonitorOrientation.Portrait : MonitorOrientation.Landscape,
            MirroringInfo.None);

        return new Monitor(identity, runtime);
    }

    private static (uint Dpi, double ScaleFactor) ReadDpi(nint monitor)
    {
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MonitorDpiTypeEffective, out var dpiX, out _) >= 0
            && dpiX > 0)
        {
            return (dpiX, dpiX / 96.0);
        }

        return (96, 1.0);
    }
}
