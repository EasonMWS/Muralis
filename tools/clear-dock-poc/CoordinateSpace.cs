namespace ClearDockPoc;

/// <summary>
/// The one place where the renderer's coordinate spaces are converted into each other.
/// </summary>
/// <remarks>
/// <para>
/// Four spaces meet in this renderer and mixing them is the classic way a dock ends up correct at 100 % and
/// subtly wrong at 150 %:
/// </para>
/// <list type="number">
/// <item><b>Screen physical pixels</b> — what raw input reports and what Win32 window rectangles use.</item>
/// <item><b>Render surface pixels</b> — the layered window's own bitmap, which is the window's client area at
/// one physical pixel per surface pixel.</item>
/// <item><b>DIP</b> — device-independent units. Every number in <c>DockMotionProfile</c> and
/// <c>DockMotionProfile</c>'s geometry is stated in DIP, because the dock must be the same physical size and
/// feel on every display.</item>
/// <item><b>Motion engine space</b> — DIP, but with its own origin: x = 0 is the left edge of the stripe's
/// content, which is where the engine's resting centres are laid out.</item>
/// </list>
/// <para>
/// The conversions are instance methods rather than scattered arithmetic so there is exactly one place where a
/// scale is applied, and so a test can check them without a window.
/// </para>
/// </remarks>
internal readonly struct CoordinateSpace
{
    /// <summary>
    /// Physical pixels per DIP for this display. 1.0 at 96 DPI, 1.5 at 144 DPI, 2.0 at 192 DPI.
    /// </summary>
    public CoordinateSpace(double scale, int windowOriginX, int windowOriginY)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 0.0001);

        Scale = scale;
        WindowOriginX = windowOriginX;
        WindowOriginY = windowOriginY;
    }

    /// <summary>Physical pixels per DIP.</summary>
    public double Scale { get; }

    /// <summary>The window's client origin in physical screen pixels.</summary>
    public int WindowOriginX { get; }

    /// <summary>The window's client origin in physical screen pixels.</summary>
    public int WindowOriginY { get; }

    /// <summary>DIP to physical pixels, rounded to the nearest whole pixel.</summary>
    public int DipToPx(double dip) => (int)Math.Round(dip * Scale, MidpointRounding.AwayFromZero);

    /// <summary>Physical pixels to DIP.</summary>
    public double PxToDip(int px) => px / Scale;

    /// <summary>A screen x in physical pixels to the render surface's own x, in surface pixels.</summary>
    public int ScreenPxToSurfacePx(int screenX) => screenX - WindowOriginX;

    /// <summary>A screen y in physical pixels to the render surface's own y, in surface pixels.</summary>
    public int ScreenPxToSurfacePxY(int screenY) => screenY - WindowOriginY;

    /// <summary>
    /// A screen x in physical pixels to motion engine space, in DIP.
    /// </summary>
    /// <remarks>
    /// Screen to surface is a subtraction; surface to DIP is a division by the scale. Keeping them as two
    /// separately named steps is what stops the scale being applied to an origin that is already in screen
    /// pixels — the mistake that makes a dock's hit test drift by a fraction of its own offset at non-100 %.
    /// </remarks>
    public double ScreenPxToMotionDip(int screenX) => PxToDip(ScreenPxToSurfacePx(screenX));

    /// <summary>A screen y in physical pixels to motion engine space, in DIP.</summary>
    public double ScreenPxToMotionDipY(int screenY) => PxToDip(ScreenPxToSurfacePxY(screenY));

    /// <summary>Motion space DIP back to a screen x in physical pixels.</summary>
    public int MotionDipToScreenPx(double dip) => WindowOriginX + DipToPx(dip);

    /// <summary>Motion space DIP back to a screen y in physical pixels.</summary>
    public int MotionDipToScreenPxY(double dip) => WindowOriginY + DipToPx(dip);
}
