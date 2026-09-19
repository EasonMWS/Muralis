namespace Muralis.Core.Motion;

/// <summary>
/// Every number the Nexus Motion Engine uses, in one place. The dock reads these; the Lab tunes them;
/// the tests pin their guarantees. Nothing on the pointer path invents a constant of its own.
/// </summary>
/// <remarks>
/// The defaults are deliberately conservative. Muralis motion is premium, calm and restrained: the
/// resting icon is enlarged about eighty per cent at most, the wave is wide enough that no single icon
/// ever looks switched on, and the lift is small enough to read as breathing rather than as a hop.
/// </remarks>
public sealed record DockMotionProfile
{
    /// <summary>The product baseline. The dock's resting icon artwork is 48 DIP.</summary>
    public static DockMotionProfile Default { get; } = new();

    /// <summary>Whether the engine moves anything at all. False leaves every icon at rest.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>The icon artwork's resting size in DIP. Only used to derive distances and reserves.</summary>
    public double BaseIconSize { get; init; } = 48;

    /// <summary>
    /// The gap the dock lays its icons out with. Not something the motion decides — it is what the dock's
    /// own panel spacing produces — but the reserve calculation has to know it to work out how wide the run
    /// can become, so it is stated once here rather than guessed at the call site.
    /// </summary>
    public double SpacingDip { get; init; } = 4;

    /// <summary>
    /// The largest the icon is drawn, as a multiple of <see cref="BaseIconSize"/>. 1.8 keeps the peak
    /// at 86.4 DIP: clearly alive, never cartoonishly large.
    /// </summary>
    public double MaxScale { get; init; } = 1.8;

    /// <summary>
    /// How far the pointer's influence reaches, as a multiple of <see cref="BaseIconSize"/>. At 2.8 the
    /// radius is 134.4 DIP, so roughly three icons either side of the pointer are involved.
    /// </summary>
    public double InfluenceRadiusInIcons { get; init; } = 2.8;

    /// <summary>The most an icon rises towards the pointer, in DIP.</summary>
    public double MaximumLift { get; init; } = 10;

    /// <summary>
    /// Extra room required beyond the expanded visual bounds before a pointer genuinely leaves. This is
    /// interaction hysteresis, not motion: it prevents a one-pixel boundary wobble from resizing the HWND.
    /// </summary>
    public double ExitMarginDip { get; init; } = 6;

    /// <summary>
    /// How much of the space the enlarged icons need is actually opened up, 0 to 1. 1 lets every
    /// neighbour move exactly as far as the widths demand, which is what keeps them from overlapping.
    /// </summary>
    public double NeighbourSpread { get; init; } = 1;

    /// <summary>How far a press shrinks the icon, before the pointer lets go again.</summary>
    public double PressScale { get; init; } = 0.94;

    /// <summary>
    /// The scale a lone or unmagnified icon takes while the pointer is over it. The magnification wave
    /// already does this job whenever the engine is running; this is what an icon does when it is the
    /// only one, or when motion is off.
    /// </summary>
    public double HoverScale { get; init; } = 1.06;

    /// <summary>How long the icons take to come back to rest once the pointer has left, in ms.</summary>
    public double SettleDurationMilliseconds { get; init; } = 220;

    /// <summary>
    /// Reduced motion: the wave is switched off entirely and there is nothing to settle. This is the
    /// profile a user who asked Windows for less animation gets.
    /// </summary>
    public static DockMotionProfile Reduced { get; } = new()
    {
        IsEnabled = false,
        SettleDurationMilliseconds = 0,
    };

    /// <summary>The influence radius in DIP.</summary>
    public double InfluenceRadius => InfluenceRadiusInIcons * BaseIconSize;

    /// <summary>The icon's size at the peak of the wave, in DIP.</summary>
    public double PeakIconSize => BaseIconSize * Math.Max(1.0, MaxScale);

    /// <summary>
    /// How much room above the resting baseline the engine needs, in DIP: the icon's own growth away
    /// from its bottom edge, plus the lift. This is the figure the dock window has to leave free, or the
    /// peak of the wave is drawn outside the window and clipped away.
    /// </summary>
    public double VerticalReserve =>
        Math.Max(0, PeakIconSize - BaseIconSize) + Math.Max(0, MaximumLift);

    /// <summary>
    /// How much room above an element of <paramref name="elementHeightDip"/> the wave needs, in DIP:
    /// that element's own growth away from its bottom edge at full scale, plus the lift.
    /// </summary>
    /// <remarks>
    /// The wave scales the element it is given and the element grows upward from its own bottom-centre, so
    /// the room it needs is a function of <em>that element's</em> height and not of the artwork inside it.
    /// The dock's magnification layer is a square around the artwork, so a stripe sized from
    /// <see cref="VerticalReserve"/> — which is derived from <see cref="BaseIconSize"/> — leaves the peak
    /// short by the difference between the two and clips it. A rounding epsilon is included because the
    /// element's own size is laid out in fractional DIP while the window is a whole number of pixels.
    /// </remarks>
    public double VerticalReserveFor(double elementHeightDip) =>
        (Math.Max(0, elementHeightDip) * Math.Max(1.0, MaxScale))
        - Math.Max(0, elementHeightDip)
        + Math.Max(0, MaximumLift)
        + ReserveRoundingEpsilonDip;

    /// <summary>
    /// The room a rounded pixel edge may take from a reserve, in DIP.
    /// </summary>
    /// <remarks>
    /// A ceiling is used to turn the stripe's height into physical pixels, so on a display scale that does
    /// not divide evenly the drawn box can sit a fraction of a DIP higher than the reserve predicted. This
    /// is the slack that keeps "the peak fits" true rather than nearly true.
    /// </remarks>
    public const double ReserveRoundingEpsilonDip = 2;

    /// <summary>True when this profile would actually move something.</summary>
    public bool IsMoving => IsEnabled && MaxScale > 1.0 && InfluenceRadius > 0;
}
