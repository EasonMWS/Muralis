namespace Muralis.Core.Motion;

/// <summary>
/// Where the dock sits and how big it is, in physical screen pixels.
/// </summary>
/// <remarks>
/// A plain rectangle of four integers rather than a platform's own: the dock's geometry is arithmetic
/// that a test drives without a window, and <c>Muralis.Core</c> may not reference the Windows SDK. The
/// window layer converts this into whatever its own API wants.
/// </remarks>
/// <param name="X">The left edge, in physical screen pixels.</param>
/// <param name="Y">The top edge, in physical screen pixels.</param>
/// <param name="Width">The width, in physical screen pixels.</param>
/// <param name="Height">The height, in physical screen pixels.</param>
public readonly record struct DockStripe(int X, int Y, int Width, int Height)
{
    /// <summary>The right edge, exclusive, in physical screen pixels.</summary>
    public int Right => X + Width;

    /// <summary>The bottom edge, exclusive, in physical screen pixels.</summary>
    public int Bottom => Y + Height;
}

/// <summary>
/// A display's work area, in physical screen pixels.
/// </summary>
/// <remarks>Introduced for the same reason as <see cref="DockStripe"/>: so the geometry stays in Core.</remarks>
/// <param name="X">The left edge, in physical screen pixels.</param>
/// <param name="Y">The top edge, in physical screen pixels.</param>
/// <param name="Width">The width, in physical screen pixels.</param>
/// <param name="Height">The height, in physical screen pixels.</param>
public readonly record struct DockWorkArea(int X, int Y, int Width, int Height);

/// <summary>
/// The Dock's product geometry: how wide it is, how tall it is, where it sits, and how much room the
/// magnification needs around it.
/// </summary>
/// <remarks>
/// <para>
/// The Dock is <b>content-sized</b>. Its resting width is the run of pinned icons plus the plate's own
/// padding, so three apps make a small dock and ten make a wider one, and nothing is sized for a dock the
/// user does not have. The width is therefore an <em>input</em> here, measured once per structural change
/// by the surface that lays the icons out; this class never measures anything, so it stays pure arithmetic
/// that a test can drive without a window.
/// </para>
/// <para>
/// <b>The reserve is not padding.</b> The stripe is laid out as three stacked parts: the room a magnified
/// icon grows into, the icon box itself, and the feet. So the room a peak needs is already inside the
/// stripe at rest, and the expanded size is only the same stripe made wider. That is what lets the window
/// grow sideways for a wave without ever moving the dock — the stripe is centred, so taking room from both
/// sides at once leaves every icon exactly where it was.
/// </para>
/// </remarks>
public static class DockStripeGeometry
{
    /// <summary>
    /// The square the Nexus motion engine actually scales, in DIP.
    /// </summary>
    /// <remarks>
    /// The stable geometric invariant of <c>DockIcon.xaml</c>'s <c>NexusMotionHost</c>, which is the
    /// element the engine writes <c>Scale</c>, <c>Translation</c> and <c>CenterPoint</c> to. It is stated
    /// here rather than read from the layout because the reserve has to be known before anything is laid
    /// out, and the Dock's own geometry checks assert it against the markup, so it cannot drift away from
    /// the thing it describes without something failing.
    /// </remarks>
    public const double IconHostDip = 52;

    /// <summary>The gap the run of icons is laid out with, in DIP. Mirrors the panel's own spacing.</summary>
    public const double IconSpacingDip = 4;

    /// <summary>The room the plate keeps to the left and right of the icons, in DIP.</summary>
    public const double PlatePaddingX = 12;

    /// <summary>The room the plate keeps below the icons, in DIP.</summary>
    public const double PlatePaddingY = 10;

    /// <summary>
    /// How far the dock's feet sit above the bottom of the work area, in DIP.
    /// </summary>
    /// <remarks>
    /// Small on purpose. The dock is meant to hug the bottom of the screen: the visible gap is this plus
    /// <see cref="PlatePaddingY"/>, and anything more starts to read as a bar with a margin under it rather
    /// than as icons floating over the wallpaper.
    /// </remarks>
    public const double BottomGapDip = 6;

    /// <summary>The narrowest dock the product draws, in DIP. It only guards against nonsense.</summary>
    public const double MinimumPlateWidthDip = IconHostDip + (2 * PlatePaddingX);

    /// <summary>How much of the screen's width the dock leaves free, so it never becomes the whole edge.</summary>
    public const double EdgeAllowanceDip = 32;

    /// <summary>
    /// How much room above the icon box the magnification needs at full scale and full lift, in DIP.
    /// </summary>
    /// <remarks>
    /// Asked of the profile rather than chosen here, and asked for the square the engine actually scales:
    /// the icon's own growth away from its bottom-centre, plus the lift. The profile's own
    /// <see cref="DockMotionProfile.VerticalReserve"/> answers a different question, because it is computed
    /// from the artwork's resting size. The artwork is smaller than the square around it, so a stripe sized
    /// from it would clip the peak of the wave by the difference.
    /// </remarks>
    public static double VerticalReserveDip => DockMotionProfile.Default.VerticalReserveFor(IconHostDip);

    /// <summary>How tall the dock's own box is, in DIP: the reserve, the icon box, and the feet.</summary>
    /// <remarks>
    /// Carried as a whole rather than derived from a measurement because the window's height must be known
    /// before anything is laid out, and because it must not change as pins come and go: a dock that changed
    /// height would move its own baseline every time an app was added.
    /// </remarks>
    public static double StripeHeightDip => VerticalReserveDip + IconHostDip + PlatePaddingY;

    /// <summary>
    /// How much room the run needs on each side at the largest wave it can produce, in DIP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things have to fit, and the larger of them wins.
    /// </para>
    /// <para>
    /// The first is how far the engine pushes the run sideways, which is asked of the engine rather than
    /// guessed from the influence radius: parked between two icons both sides of the wave take their full
    /// share at once, and that is the case a formula based on one icon's half-width misses.
    /// </para>
    /// <para>
    /// The second is the peak icon's own half-width. The engine measures displacement from where an icon
    /// <em>rests</em>, so an icon at the end of the run reports no displacement while still growing to
    /// <see cref="DockMotionProfile.MaxScale"/> about its own centre, half of which is therefore drawn
    /// beyond the run. On a long dock the engine's own reach is far larger and this never binds; on a short
    /// one it is the whole reserve, and leaving it out is what clips a three-app dock at the peak.
    /// </para>
    /// </remarks>
    public static double HorizontalReserveDip(int itemCount)
    {
        if (itemCount <= 0)
        {
            return 0;
        }

        var profile = DockMotionProfile.Default;
        var pushed = DockMotionEngine.ReserveFor(itemCount, IconHostDip, IconSpacingDip, profile);
        var peakHalfWidth = profile.IsMoving
            ? (IconHostDip * Math.Max(1.0, profile.MaxScale)) / 2
            : 0;

        return Math.Max(pushed, peakHalfWidth);
    }

    /// <summary>Where the dock sits, and how big it is, while nothing is magnified.</summary>
    /// <param name="contentWidthDip">
    /// The width the run of icons actually occupies, in DIP, or zero when there is nothing pinned. The
    /// plate's own padding is added here, so the caller passes the content and not the plate.
    /// </param>
    /// <param name="workArea">The work area of the display the dock is on.</param>
    /// <param name="pixelsPerDip">How many physical screen pixels make one dock DIP.</param>
    public static DockStripe Resting(double contentWidthDip, DockWorkArea workArea, double pixelsPerDip)
    {
        var scale = NormalizeScale(pixelsPerDip);
        var width = PlateWidth(contentWidthDip, workArea.Width, scale);
        var height = ToPixels(StripeHeightDip, scale);
        return new DockStripe(Centred(workArea, width), BottomOf(workArea, height, scale), width, height);
    }

    /// <summary>Where the dock sits, and how big it is, while the pointer is on it.</summary>
    /// <param name="contentWidthDip">The width the run of icons occupies, in DIP.</param>
    /// <param name="iconCount">How many icons the run holds, which is what the sideways reserve follows.</param>
    /// <param name="workArea">The work area of the display the dock is on.</param>
    /// <param name="pixelsPerDip">How many physical screen pixels make one dock DIP.</param>
    /// <remarks>
    /// Wider by the sideways reserve on <em>both</em> sides, and the same height: the room a peak needs
    /// above the icons is already in the stripe. The stripe is centred, so growing the window around it
    /// takes the room from either side and every icon stays exactly where it was drawn.
    /// </remarks>
    public static DockStripe Expanded(double contentWidthDip, int iconCount, DockWorkArea workArea, double pixelsPerDip)
    {
        var scale = NormalizeScale(pixelsPerDip);
        var reserve = HorizontalReserveDip(iconCount);
        var width = PlateWidth(contentWidthDip + (2 * reserve), workArea.Width, scale);

        // The expanded window must still be able to hold the room the sideways reach is spent in, even on a
        // work area too narrow to give it: an expanded window narrower than the reserve would clip the wave
        // it has just made room for.
        width = Math.Max(width, PlateWidth(2 * reserve, workArea.Width, scale));

        var height = ToPixels(StripeHeightDip, scale);
        return new DockStripe(Centred(workArea, width), BottomOf(workArea, height, scale), width, height);
    }

    /// <summary>
    /// How wide the window is for a given content width, in physical pixels: the icons, the plate around
    /// them, kept above the product's floor and inside the screen.
    /// </summary>
    private static int PlateWidth(double contentWidthDip, int workAreaWidth, double scale)
    {
        var plate = Math.Max(0, contentWidthDip) + (2 * PlatePaddingX);
        var floor = ToPixels(MinimumPlateWidthDip, scale);
        var ceiling = Math.Max(floor, workAreaWidth - ToPixels(EdgeAllowanceDip, scale));
        var wanted = Math.Max(floor, ToPixels(plate, scale));

        // The ceiling is applied last, so a work area narrower than the floor still yields a dock.
        return Math.Min(wanted, ceiling);
    }

    private static int Centred(DockWorkArea workArea, int width) =>
        workArea.X + ((workArea.Width - width) / 2);

    /// <summary>
    /// The window's top edge for a window of <paramref name="height"/> anchored the way the dock needs it.
    /// </summary>
    /// <remarks>
    /// The stripe's own feet are part of its height, so the visible gap between the icons and the bottom of
    /// the work area is <see cref="PlatePaddingY"/> plus <see cref="BottomGapDip"/>. A work area too short
    /// for the stripe is not a reason to push the dock off the display: the room is given up from the top
    /// instead, and the peak is clipped rather than the dock lost.
    /// </remarks>
    private static int BottomOf(DockWorkArea workArea, int height, double scale) =>
        Math.Max(workArea.Y + workArea.Height - ToPixels(BottomGapDip + PlatePaddingY, scale) - height, workArea.Y);

    private static int ToPixels(double dips, double scale) => (int)Math.Ceiling(dips * scale);

    private static double NormalizeScale(double scale) =>
        double.IsFinite(scale) && scale > 0 ? scale : 1;
}
