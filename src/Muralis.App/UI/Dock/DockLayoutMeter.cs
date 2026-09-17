using System.Globalization;
using Microsoft.UI.Xaml;
using Muralis.Core.Diagnostics;

namespace Muralis.App.UI.Dock;

/// <summary>
/// Records what a drop costs after the release handler has returned: the layout passes the new order
/// forces, where the items control moves its containers and every icon re-reads its state.
/// </summary>
/// <remarks>
/// A profiler scope cannot see that work — it happens on a layout pass of its own, after the callback
/// that asked for it — so it is timed from the outside against the same clock the segments use. Only
/// exists when the profiler is on; with it off the dock never subscribes to layout at all.
/// </remarks>
internal sealed class DockLayoutMeter
{
    private const int Passes = 2;

    // A pass that arrives later than this after the one before it is not the layout the drop caused; it
    // is whatever touched the window next, and the benchmark's own window probes arrive exactly so.
    private const double FollowWindowMs = 120;

    private readonly FrameworkElement _root;
    private double _armedAtMs;
    private double _lastMs;
    private double _lastCpuMs;
    private int _remaining;

    public DockLayoutMeter(FrameworkElement root)
    {
        _root = root;
        _root.LayoutUpdated += OnLayoutUpdated;
    }

    /// <summary>
    /// Starts watching. Called from the callback that has just redrawn the zone, which is the last thing
    /// to happen before the layout pass being timed.
    /// </summary>
    public void Arm()
    {
        _armedAtMs = DropProfile.ElapsedMs;
        _lastMs = _armedAtMs;
        _lastCpuMs = DropProfile.CurrentThreadCpuMs;
        _remaining = Passes;
    }

    private void OnLayoutUpdated(object? sender, object args)
    {
        if (_remaining <= 0)
        {
            return;
        }

        var nowMs = DropProfile.ElapsedMs;
        if (nowMs - _lastMs > FollowWindowMs)
        {
            _remaining = 0;
            return;
        }

        _remaining--;
        var nowCpu = DropProfile.CurrentThreadCpuMs;
        DropProfile.Mark(
            "layout.pass" + (Passes - _remaining).ToString(CultureInfo.InvariantCulture),
            nowMs - _lastMs,
            "\"thCpu\":" + Number(nowCpu - _lastCpuMs)
            + ",\"th\":" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)
            + ",\"since\":\"" + (nowMs - _armedAtMs).ToString("F3", CultureInfo.InvariantCulture) + "\"");

        _lastMs = nowMs;
        _lastCpuMs = nowCpu;
    }

    private static string Number(double value) =>
        double.IsNaN(value) ? "null" : value.ToString("F3", CultureInfo.InvariantCulture);
}
