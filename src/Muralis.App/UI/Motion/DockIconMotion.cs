using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Muralis.App.UI.Motion;

/// <summary>
/// The one place that writes to a dock icon's magnification layer.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UIElement.Scale"/> and <see cref="UIElement.Translation"/> are composition-backed
/// replacement properties: on any one element they are mutually exclusive with <c>RenderTransform</c>,
/// <c>Projection</c> and <c>Transform3D</c>. Writing both sets on the same element fails at runtime, which
/// is why the magnification layer is a separate element from the one the drag owns, and why every write to
/// it goes through this class.
/// </para>
/// <para>
/// Scale/Translation/CenterPoint belong to the Nexus layer. RenderTransform belongs to a different interaction
/// layer. The methods live together to keep motion policy central, but callers must pass those distinct elements.
/// </para>
/// </remarks>
internal static class DockIconMotion
{
    /// <summary>
    /// The origin of a magnification, at the bottom centre of the icon box. The layer is that box, so the
    /// bottom centre is half its width and all of its height.
    /// </summary>
    private static readonly Vector3 Origin = new(26f, 52f, 0f);

    private const int RestingDepth = 8;
    private const int RaisedDepth = 22;
    private const int PressedDepth = 4;

    /// <summary>Where an icon sits in the dock's own z-order.</summary>
    internal enum Depth
    {
        /// <summary>Nothing is happening to this icon.</summary>
        Resting,

        /// <summary>The pointer is on it, or it was just released.</summary>
        Raised,

        /// <summary>The pointer is down on it.</summary>
        Pressed,
    }

    /// <summary>
    /// Puts the origin of the magnification at the bottom centre of the icon box, so a magnified icon grows
    /// upward and sideways from the dock's baseline instead of from its own middle.
    /// </summary>
    /// <remarks>
    /// A constant of the layout rather than a measurement, so it is written once on load. Nothing on the
    /// pointer path is allowed to measure anything.
    /// </remarks>
    public static void PrepareOrigin(FrameworkElement layer) => layer.CenterPoint = Origin;

    /// <summary>
    /// Draws one icon at its magnified size and place. This is the pointer-tracking write and it runs on
    /// every pointer move, so it writes only what has actually changed: a compositor property written with
    /// the value it already holds is pure cost.
    /// </summary>
    /// <remarks>
    /// Only X, Y and the scale are this method's business. Draw order is an attached panel property on the
    /// outer host and belongs to <see cref="SetDepth"/>, so it never shares this composition channel.
    /// </remarks>
    public static void ApplyMagnification(FrameworkElement layer, double scale, double translateX, double lift)
    {
        var expectedScale = new Vector3((float)scale, (float)scale, 1f);
        if (layer.Scale != expectedScale)
        {
            layer.Scale = expectedScale;
        }

        // Lift is upward, so it is negative in Y.
        var expectedOffset = new Vector3((float)translateX, (float)-lift, 0f);
        if (layer.Translation != expectedOffset)
        {
            layer.Translation = expectedOffset;
        }
    }

    /// <summary>
    /// Hands an icon back to its resting size and place. Used whenever the engine is not driving it: the
    /// pointer is away, motion is switched off, or a drag has taken the icon over.
    /// </summary>
    public static void Release(FrameworkElement layer)
    {
        if (layer.Scale != Vector3.One)
        {
            layer.Scale = Vector3.One;
        }

        if (layer.Translation != Vector3.Zero)
        {
            layer.Translation = Vector3.Zero;
        }
    }

    /// <summary>
    /// The icon's own hover and press preview, on the XAML channel. Kept deliberately small: the
    /// magnification already says "the pointer is here", so this only has to say "this one is clickable".
    /// </summary>
    public static void SetLocalScale(FrameworkElement layer, double scale, bool lift)
    {
        var translateY = lift ? MotionDurations.HoverLift : 0;

        if (!MotionPreferences.AnimationsEnabled)
        {
            TransformMotion.Set(layer, scale, translateY);
            return;
        }

        TransformMotion.Animate(
            layer,
            scale,
            translateY,
            MotionDurations.Standard,
            new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    /// <summary>
    /// Clears interaction motion synchronously before Nexus becomes active. An animated return would briefly
    /// multiply the old hover scale by the Nexus peak even though the transforms live on different elements.
    /// </summary>
    public static void ResetLocalScale(FrameworkElement layer) => TransformMotion.Set(layer, 1, 0);

    /// <summary>Moves an icon up or down the dock's own z-order.</summary>
    /// <remarks>
    /// The order that matters belongs to the <em>icon</em>, because an icon's siblings are the other icons in
    /// the dock's items panel, and that is the population the order is relative to. This used to name
    /// <c>MotionHost</c>, whose siblings are the label row and the running indicator inside one icon — so a
    /// hovered or pressed icon never actually came forward over its neighbours.
    /// </remarks>
    public static void SetDepth(FrameworkElement icon, Depth depth)
    {
        var z = depth switch
        {
            Depth.Raised => RaisedDepth,
            Depth.Pressed => PressedDepth,
            _ => RestingDepth,
        };

        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(icon, z);
    }
}
