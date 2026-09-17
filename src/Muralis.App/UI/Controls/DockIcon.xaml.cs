using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.UI.Motion;
using System.Numerics;

namespace Muralis.App.UI.Controls;

/// <summary>
/// Composition-ready icon shell for the future Dock. The magnification engine can
/// animate <see cref="MotionTarget"/> directly and disable the local pointer preview.
/// </summary>
public sealed partial class DockIcon : UserControl
{
    /// <summary>The hover scale this icon uses.</summary>
    private const double HoverScale = 1.08;

    public static readonly DependencyProperty IconContentProperty = DependencyProperty.Register(
        nameof(IconContent), typeof(object), typeof(DockIcon), new PropertyMetadata(null));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(DockIcon), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(DockIcon), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(DockIcon), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty IsUnavailableProperty = DependencyProperty.Register(
        nameof(IsUnavailable), typeof(bool), typeof(DockIcon), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty IsHighlightedProperty = DependencyProperty.Register(
        nameof(IsHighlighted), typeof(bool), typeof(DockIcon), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty IsPressedPreviewProperty = DependencyProperty.Register(
        nameof(IsPressedPreview), typeof(bool), typeof(DockIcon), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty UsePointerMotionProperty = DependencyProperty.Register(
        nameof(UsePointerMotion), typeof(bool), typeof(DockIcon), new PropertyMetadata(true));

    private bool _pointerOver;

    public DockIcon()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateState();
            SetDepth(8);
        };
        IsEnabledChanged += (_, _) => UpdateState();
        PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            UpdateState();
            if (UsePointerMotion)
            {
                HoverMotion.Enter(MotionHost, lift: true, scale: HoverScale);
                SetDepth(22);
            }
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            UpdateState();
            if (UsePointerMotion)
            {
                HoverMotion.Exit(MotionHost);
                SetDepth(8);
            }
        };
        PointerPressed += (_, _) =>
        {
            if (UsePointerMotion)
            {
                PressMotion.Down(MotionHost);
                SetDepth(4);
            }
        };
        PointerReleased += (_, _) =>
        {
            if (UsePointerMotion)
            {
                PressMotion.Release(MotionHost, _pointerOver, lift: true, hoverScale: HoverScale);
                SetDepth(_pointerOver ? 22 : 8);
            }
        };
    }

    public object? IconContent
    {
        get => GetValue(IconContentProperty);
        set => SetValue(IconContentProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsRunning
    {
        get => (bool)GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>True when the thing this pin points at is gone. The pin stays usable as a menu target.</summary>
    public bool IsUnavailable
    {
        get => (bool)GetValue(IsUnavailableProperty);
        set => SetValue(IsUnavailableProperty, value);
    }

    /// <summary>Marks the pin the user was just pointed at, without changing what it does.</summary>
    public bool IsHighlighted
    {
        get => (bool)GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    public bool IsPressedPreview
    {
        get => (bool)GetValue(IsPressedPreviewProperty);
        set => SetValue(IsPressedPreviewProperty, value);
    }

    public bool UsePointerMotion
    {
        get => (bool)GetValue(UsePointerMotionProperty);
        set => SetValue(UsePointerMotionProperty, value);
    }

    public FrameworkElement MotionTarget => MotionHost;

    /// <summary>
    /// Takes the hover preview back after something else was driving the icon — a drag, which turns
    /// <see cref="UsePointerMotion"/> off while it owns the pointer. Whether the pointer is still over
    /// the icon is this icon's own business, so it is decided here rather than by the caller.
    /// </summary>
    internal void ResumePointerMotion()
    {
        UsePointerMotion = true;
        if (_pointerOver)
        {
            HoverMotion.Enter(MotionHost, lift: true, scale: HoverScale);
            SetDepth(22);
        }
        else
        {
            HoverMotion.Exit(MotionHost);
            SetDepth(8);
        }
    }

    private static void OnStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((DockIcon)dependencyObject).UpdateState();

    private void UpdateState()
    {
        if (IconSurface is null)
        {
            return;
        }

        var active = IsSelected || IsHighlighted;

        RunningIndicator.Opacity = IsRunning ? 1 : 0;
        SelectionIndicator.Opacity = active ? 1 : 0;
        UnavailableBadge.Opacity = IsUnavailable ? 1 : 0;
        IconSurface.Opacity = IsUnavailable ? 0.45 : 1;
        Opacity = IsEnabled ? 1 : 0.42;

        LabelText.Foreground = Brush(IsUnavailable ? "MuralisTextTertiaryBrush" : "MuralisTextSecondaryBrush");

        if (active)
        {
            IconSurface.Background = Brush("MuralisGlassMediumBrush");
            IconSurface.BorderBrush = Brush("MuralisBorderActiveBrush");
        }
        else if (_pointerOver)
        {
            IconSurface.Background = Brush("MuralisSurfaceHighBrush");
            IconSurface.BorderBrush = Brush("MuralisBorderHoverBrush");
        }
        else
        {
            IconSurface.Background = Brush("MuralisGlassLowBrush");
            IconSurface.BorderBrush = Brush("MuralisBorderSubtleBrush");
        }

        if (IsPressedPreview)
        {
            PressMotion.Down(MotionHost);
        }
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string token) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[token];

    private void SetDepth(float z) => MotionHost.Translation = new Vector3(
        MotionHost.Translation.X,
        MotionHost.Translation.Y,
        z);
}
