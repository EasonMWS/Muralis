namespace Muralis.Core.DockShell;

public enum DockZoneKind
{
    PinnedApps,
    DesktopShelf,
    Utilities,
}

/// <summary>Layout constants for a floating, content-sized Dock shell.</summary>
public sealed record DockShellLayoutOptions
{
    public double ItemWidth { get; init; } = 68;

    public double ItemSpacing { get; init; } = 4;

    public double ZoneGap { get; init; } = 12;

    public double HorizontalPadding { get; init; } = 14;

    public double ShelfMinimumWidth { get; init; } = 220;

    public double ShelfPreferredWidth { get; init; } = 420;

    public double ShelfMaximumWidth { get; init; } = 560;

    public void Validate()
    {
        if (ItemWidth <= 0 || ItemSpacing < 0 || ZoneGap < 0 || HorizontalPadding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ItemWidth), "Dock sizes must be positive and spacing cannot be negative.");
        }

        if (ShelfMinimumWidth <= 0
            || ShelfPreferredWidth < ShelfMinimumWidth
            || ShelfMaximumWidth < ShelfPreferredWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(ShelfMinimumWidth), "Shelf widths must satisfy minimum <= preferred <= maximum.");
        }
    }
}

public sealed record DockZoneLayout(
    DockZoneKind Zone,
    double ViewportWidth,
    double ContentWidth,
    bool IsScrollable);

public sealed record DockShellLayoutResult(
    DockZoneLayout PinnedApps,
    DockZoneLayout DesktopShelf,
    DockZoneLayout Utilities,
    double TotalWidth);

/// <summary>
/// Pure layout policy: pinned apps and utilities always retain their intrinsic widths; only the middle
/// Shelf receives a bounded viewport and overflows horizontally.
/// </summary>
public static class DockShellLayout
{
    public static DockShellLayoutResult Calculate(
        int pinnedCount,
        int shelfCount,
        int utilityCount,
        double availableWidth = double.PositiveInfinity,
        DockShellLayoutOptions? options = null)
    {
        if (pinnedCount < 0 || shelfCount < 0 || utilityCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pinnedCount));
        }

        options ??= new DockShellLayoutOptions();
        options.Validate();

        var pinnedWidth = ItemsWidth(pinnedCount, options);
        var shelfContentWidth = ItemsWidth(shelfCount, options);
        var utilityWidth = ItemsWidth(utilityCount, options);
        var fixedWidth = pinnedWidth + utilityWidth + (options.HorizontalPadding * 2) + (options.ZoneGap * 2);

        var shelfViewport = Math.Clamp(
            Math.Max(options.ShelfMinimumWidth, Math.Min(shelfContentWidth, options.ShelfPreferredWidth)),
            options.ShelfMinimumWidth,
            options.ShelfMaximumWidth);

        if (double.IsFinite(availableWidth))
        {
            shelfViewport = Math.Max(options.ShelfMinimumWidth, Math.Min(shelfViewport, availableWidth - fixedWidth));
        }

        return new DockShellLayoutResult(
            new DockZoneLayout(DockZoneKind.PinnedApps, pinnedWidth, pinnedWidth, false),
            new DockZoneLayout(
                DockZoneKind.DesktopShelf,
                shelfViewport,
                shelfContentWidth,
                shelfContentWidth > shelfViewport),
            new DockZoneLayout(DockZoneKind.Utilities, utilityWidth, utilityWidth, false),
            fixedWidth + shelfViewport);
    }

    private static double ItemsWidth(int count, DockShellLayoutOptions options) =>
        count == 0 ? 0 : (count * options.ItemWidth) + ((count - 1) * options.ItemSpacing);
}
