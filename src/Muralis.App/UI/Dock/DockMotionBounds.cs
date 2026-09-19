using Muralis.Core.Motion;

namespace Muralis.App.UI.Dock;

/// <summary>
/// The pair of sizes the dock window can be, in the WinRT rectangle the window API wants.
/// </summary>
/// <remarks>
/// A thin adapter and deliberately nothing more: every number comes from <see cref="DockStripeGeometry"/>,
/// which is pure arithmetic with no window in it and is therefore where the product's geometry rules are
/// actually tested. This type exists only to carry those rules across the one API boundary that needs a
/// <see cref="Windows.Graphics.RectInt32"/>.
/// </remarks>
internal static class DockMotionBounds
{
    /// <summary>How wide the window is when nothing is magnified.</summary>
    public static Windows.Graphics.RectInt32 Resting(
        double contentWidthDip,
        Windows.Graphics.RectInt32 workArea,
        double pixelsPerDip)
    {
        var stripe = DockStripeGeometry.Resting(contentWidthDip, Area(workArea), pixelsPerDip);
        return new Windows.Graphics.RectInt32(stripe.X, stripe.Y, stripe.Width, stripe.Height);
    }

    /// <summary>How wide the window is while the pointer is on the dock.</summary>
    public static Windows.Graphics.RectInt32 Expanded(
        double contentWidthDip,
        int iconCount,
        Windows.Graphics.RectInt32 workArea,
        double pixelsPerDip)
    {
        var stripe = DockStripeGeometry.Expanded(contentWidthDip, iconCount, Area(workArea), pixelsPerDip);
        return new Windows.Graphics.RectInt32(stripe.X, stripe.Y, stripe.Width, stripe.Height);
    }

    private static DockWorkArea Area(Windows.Graphics.RectInt32 workArea) =>
        new(workArea.X, workArea.Y, workArea.Width, workArea.Height);
}
