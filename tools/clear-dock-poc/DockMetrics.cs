namespace ClearDockPoc;

/// <summary>
/// The dock's geometry, held in both coordinate spaces at once and derived from one scale.
/// </summary>
/// <remarks>
/// <para>
/// Every DIP member is a product number: the icon box, the pitch, the padding and the reserve are the dock's
/// design, and they must not change with the display. Every Px member is that same number multiplied by the
/// display's scale exactly once, here. Nothing downstream multiplies a scale again.
/// </para>
/// <para>
/// Keeping both in one immutable value is deliberate. The alternative — passing DIP around and converting at
/// each use — is how a renderer ends up with an icon drawn at one size and hit-tested at another, and the bug
/// only appears on a display whose scale is not 1.
/// </para>
/// </remarks>
internal readonly struct DockMetrics
{
    /// <summary>The icon box: the square the artwork is drawn into, in DIP.</summary>
    public const double IconBoxDip = 52;

    /// <summary>The horizontal pitch from one icon's slot to the next, in DIP.</summary>
    public const double CellDip = 56;

    /// <summary>The stripe's own inset at each end, in DIP.</summary>
    public const double PaddingDip = 12;

    /// <summary>Room below the icons for the stripe's feet, in DIP.</summary>
    public const double PlatePaddingYDip = 10;

    /// <summary>Room above the icons for a magnified icon to grow into, in DIP.</summary>
    public const double VerticalReserveDip = 44;

    /// <summary>Gap between the stripe's bottom edge and the bottom of the screen, in DIP.</summary>
    public const double BottomGapDip = 24;

    public DockMetrics(double scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 0.0001);

        Scale = scale;
        IconBoxPx = (int)Math.Round(IconBoxDip * scale, MidpointRounding.AwayFromZero);
        CellPx = (int)Math.Round(CellDip * scale, MidpointRounding.AwayFromZero);
        PaddingPx = (int)Math.Round(PaddingDip * scale, MidpointRounding.AwayFromZero);
        PlatePaddingYPx = (int)Math.Round(PlatePaddingYDip * scale, MidpointRounding.AwayFromZero);
        VerticalReservePx = (int)Math.Round(VerticalReserveDip * scale, MidpointRounding.AwayFromZero);
        BottomGapPx = (int)Math.Round(BottomGapDip * scale, MidpointRounding.AwayFromZero);
    }

    public double Scale { get; }

    public int IconBoxPx { get; }

    public int CellPx { get; }

    public int PaddingPx { get; }

    public int PlatePaddingYPx { get; }

    public int VerticalReservePx { get; }

    public int BottomGapPx { get; }

    /// <summary>The gap between two adjacent icon boxes, in DIP. Fixed by the layout, not by the icons' scale.</summary>
    public double GapDip => CellDip - IconBoxDip;

    /// <summary>
    /// The window's height in physical pixels: the reserve, the icon box and the feet.
    /// </summary>
    public int StripeHeightPx => VerticalReservePx + IconBoxPx + PlatePaddingYPx;

    /// <summary>
    /// The window's width in physical pixels for <paramref name="pins"/> icons: every icon, the gaps between
    /// them, and the stripe's inset at each end. There is no gap after the last icon.
    /// </summary>
    public int StripeWidthPx(int pins) =>
        (pins * CellPx) - (CellPx - IconBoxPx) + (2 * PaddingPx);

    /// <summary>
    /// The y the icons stand on, in surface pixels: the bottom of the icon band.
    /// </summary>
    public int RestingBaselinePx => StripeHeightPx - PlatePaddingYPx;

    /// <summary>
    /// The size artwork is read from the shell at, in physical pixels.
    /// </summary>
    /// <remarks>
    /// The largest this icon can ever be drawn, so that one shell read covers every scale the magnification can
    /// reach and no frame ever has to go back to the shell. Reading at the resting size and then scaling up to
    /// 1.8 would invent the detail out of nothing, which is exactly the chunky pixels this replaced.
    /// </remarks>
    public int ArtworkPx(double maxScale) =>
        (int)Math.Ceiling(IconBoxDip * Scale * maxScale);
}
