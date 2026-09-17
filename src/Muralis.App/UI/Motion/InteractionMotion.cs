using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Muralis.App.UI.Motion;

/// <summary>
/// Shared transform-only motion primitives. They avoid layout changes and keep the
/// timing contract in one place so controls cannot invent their own animation pace.
/// </summary>
public static class HoverMotion
{
    public static void Enter(FrameworkElement element, bool lift = true, double scale = MotionDurations.HoverScale) =>
        TransformMotion.Animate(element, scale, lift ? MotionDurations.HoverLift : 0, MotionDurations.Standard, new CubicEase { EasingMode = EasingMode.EaseOut });

    public static void Exit(FrameworkElement element) =>
        TransformMotion.Animate(element, 1, 0, MotionDurations.Standard, new CubicEase { EasingMode = EasingMode.EaseOut });
}

public static class PressMotion
{
    public static void Down(FrameworkElement element) =>
        TransformMotion.Animate(element, MotionDurations.PressScale, 0, MotionDurations.Fast, new CubicEase { EasingMode = EasingMode.EaseOut });

    public static void Release(FrameworkElement element, bool pointerOver = true, bool lift = false, double hoverScale = MotionDurations.HoverScale) =>
        SpringMotion.Settle(element, pointerOver ? hoverScale : 1, pointerOver && lift ? MotionDurations.HoverLift : 0);
}

public static class EntranceMotion
{
    public static void Run(FrameworkElement element)
    {
        if (!MotionPreferences.AnimationsEnabled)
        {
            element.Opacity = 1;
            TransformMotion.Set(element, 1, 0);
            return;
        }

        element.Opacity = 0;
        TransformMotion.EnsureTransform(element).TranslateY = 8;

        var opacity = new DoubleAnimation
        {
            To = 1,
            Duration = MotionDurations.Slow,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));

        var translate = TransformMotion.CreateAnimation(0, MotionDurations.Slow, new CubicEase { EasingMode = EasingMode.EaseOut });
        var transform = TransformMotion.EnsureTransform(element);
        Storyboard.SetTarget(translate, transform);
        Storyboard.SetTargetProperty(translate, nameof(CompositeTransform.TranslateY));

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(translate);
        storyboard.Begin();
    }
}

public static class SpringMotion
{
    public static void Settle(FrameworkElement element, double scale, double translateY) =>
        TransformMotion.Animate(
            element,
            scale,
            translateY,
            MotionDurations.Standard,
            new BackEase { Amplitude = 0.12, EasingMode = EasingMode.EaseOut });
}

public static class MotionDurations
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(120));
    public static readonly Duration Standard = new(TimeSpan.FromMilliseconds(180));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(280));

    public const double HoverScale = 1.012;
    public const double PressScale = 0.985;
    public const double HoverLift = -2;
}

/// <summary>
/// Honors the Windows animation preference. The override keeps previews and future
/// Dock composition code able to take ownership without duplicating policy checks.
/// </summary>
public static class MotionPreferences
{
    public static bool? AnimationsEnabledOverride { get; set; }

    public static bool AnimationsEnabled
    {
        get
        {
            if (AnimationsEnabledOverride is bool value)
            {
                return value;
            }

            try
            {
                return new UISettings().AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }
}

internal static class TransformMotion
{
    public static CompositeTransform EnsureTransform(FrameworkElement element)
    {
        if (element.RenderTransform is CompositeTransform transform)
        {
            return transform;
        }

        transform = new CompositeTransform();
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return transform;
    }

    public static void Animate(FrameworkElement element, double scale, double translateY, Duration duration, EasingFunctionBase easing)
    {
        if (!MotionPreferences.AnimationsEnabled)
        {
            Set(element, scale, translateY);
            return;
        }

        var transform = EnsureTransform(element);
        var storyboard = new Storyboard();
        Add(storyboard, transform, nameof(CompositeTransform.ScaleX), scale, duration, easing);
        Add(storyboard, transform, nameof(CompositeTransform.ScaleY), scale, duration, easing);
        Add(storyboard, transform, nameof(CompositeTransform.TranslateY), translateY, duration, easing);
        storyboard.Begin();
    }

    public static void Set(FrameworkElement element, double scale, double translateY)
    {
        var transform = EnsureTransform(element);
        transform.ScaleX = scale;
        transform.ScaleY = scale;
        transform.TranslateY = translateY;
    }

    /// <summary>
    /// Moves an element sideways with no animation at all. This is the one used while the pointer is
    /// down: a drag that eased toward its target would leave the thing being carried behind the hand.
    /// </summary>
    public static void SetTranslateX(FrameworkElement element, double translateX) =>
        EnsureTransform(element).TranslateX = translateX;

    /// <summary>
    /// Eases an element sideways and completes when it has arrived. Used only to let a released drag
    /// settle, which is why the caller is told when the movement is over: the translation it was
    /// written over has to be cleared after the animation, never before.
    /// </summary>
    public static Task AnimateTranslateXAsync(
        FrameworkElement element,
        double translateX,
        Duration duration,
        EasingFunctionBase easing)
    {
        var transform = EnsureTransform(element);
        if (!MotionPreferences.AnimationsEnabled)
        {
            transform.TranslateX = translateX;
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = CreateAnimation(translateX, duration, easing);
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, nameof(CompositeTransform.TranslateX));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => completion.TrySetResult();
        storyboard.Begin();
        return completion.Task;
    }

    public static DoubleAnimation CreateAnimation(double value, Duration duration, EasingFunctionBase easing) =>
        new()
        {
            To = value,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = false,
        };

    private static void Add(Storyboard storyboard, CompositeTransform transform, string property, double value, Duration duration, EasingFunctionBase easing)
    {
        var animation = CreateAnimation(value, duration, easing);
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
