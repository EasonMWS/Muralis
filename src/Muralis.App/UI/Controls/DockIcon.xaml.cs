using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.UI.Motion;

namespace Muralis.App.UI.Controls;

/// <summary>
/// One icon in the dock: the shell picture, its label, its running indicator, and the magnification layer
/// the Nexus motion engine drives.
/// </summary>
/// <remarks>
/// <para>
/// There are three transform layers here and they belong to different owners.
/// <see cref="MotionHost"/> is the whole item and its <c>RenderTransform</c> is the drag's: a carried icon
/// is translated by the raw pointer delta on that layer.
/// <c>NexusMotionHost</c> is the square icon box inside it and it is the magnification engine's: the
/// engine writes <c>Scale</c> and <c>Translation</c> there and nothing else may.
/// <c>InteractionHost</c> owns the independent hover/press <c>RenderTransform</c>.
/// </para>
/// <para>
/// This icon's own hover and press preview is the only thing that has to choose a side. It takes the XAML
/// channel on the interaction layer. While Nexus is active that scale feedback is disabled, so the 1.08
/// hover preview can never multiply the engine's peak to roughly 1.94.
/// </para>
/// </remarks>
public sealed partial class DockIcon : UserControl
{
    /// <summary>The hover scale this icon uses when nothing else is driving it.</summary>
    private const double HoverScale = 1.08;

    /// <summary>The press scale, so a click reads as going into the dock.</summary>
    private const double PressScale = 0.985;

    public static readonly DependencyProperty IconContentProperty = DependencyProperty.Register(
        nameof(IconContent), typeof(object), typeof(DockIcon), new PropertyMetadata(null));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(DockIcon), new PropertyMetadata(string.Empty, OnLabelChanged));
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
        nameof(UsePointerMotion), typeof(bool), typeof(DockIcon), new PropertyMetadata(true, OnPointerMotionChanged));

    private bool _pointerOver;
    private bool _nexusMotionActive;

    public DockIcon()
    {
        InitializeComponent();

        // The two transform layers name themselves for assistive tools, and that naming is also what makes
        // the magnification measurable from outside the process: the rectangle a UI Automation client reads
        // for an element is the rectangle the compositor drew, so a magnified icon reports a larger one.
        // Set here rather than in markup so the dock's XAML stays about how the dock looks.
        AutomationProperties.SetAutomationId(MotionHost, "DockIconMotionHost");
        AutomationProperties.SetAutomationId(NexusMotionHost, "DockIconMotion");
        NameIcon();

        // The dock draws no label, so the pin's name is carried by this element rather than by text in the
        // layout. Applied here as well as from the property callback so a label that arrives after the
        // control is built still lands.
        NameIcon();

        Loaded += (_, _) =>
        {
            DockIconMotion.PrepareOrigin(NexusMotionHost);
            DockIconMotion.SetDepth(this, DockIconMotion.Depth.Resting);
            UpdateState();
        };
        IsEnabledChanged += (_, _) => UpdateState();
        PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            UpdateState();
            ApplyLocalMotion();
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            UpdateState();
            ApplyLocalMotion();
        };
        PointerPressed += (_, _) =>
        {
            if (UsePointerMotion)
            {
                if (!_nexusMotionActive)
                {
                    DockIconMotion.SetLocalScale(InteractionHost, PressScale, lift: false);
                }

                DockIconMotion.SetDepth(this, DockIconMotion.Depth.Pressed);
            }
        };
        PointerReleased += (_, _) =>
        {
            ApplyLocalMotion();
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

    /// <summary>
    /// Whether this icon may preview the pointer itself. The drag clears it while it owns the pointer, and
    /// so does the magnification engine when it takes the icon over — two things animating one element from
    /// different rules is how a dock starts to look elastic.
    /// </summary>
    public bool UsePointerMotion
    {
        get => (bool)GetValue(UsePointerMotionProperty);
        set => SetValue(UsePointerMotionProperty, value);
    }

    /// <summary>
    /// The element the Nexus motion engine drives: the square icon box, whose scale origin is its own bottom
    /// centre. The label and the running indicator sit outside it and are therefore not magnified.
    /// </summary>
    public FrameworkElement MotionTarget => NexusMotionHost;

    /// <summary>
    /// The element this icon's own hover and press preview is drawn on.
    /// </summary>
    /// <remarks>
    /// Exposed because it is the only element a caller may hand to the interaction motion helpers. The
    /// <see cref="MotionTarget"/> already carries <c>CenterPoint</c> for the magnification, and WinUI does not
    /// allow <c>RenderTransform</c> on an element that does: asking for the hover preview there throws
    /// <c>UnauthorizedAccessException</c> rather than failing quietly. Anything that wants to take this icon's
    /// preview away — a drag does — must do it here.
    /// </remarks>
    public FrameworkElement InteractionLayer => InteractionHost;

    /// <summary>Separates Nexus ownership from hover/press without changing drag's pointer ownership.</summary>
    internal void SetNexusMotionActive(bool active)
    {
        if (_nexusMotionActive == active)
        {
            return;
        }

        _nexusMotionActive = active;
        if (active)
        {
            DockIconMotion.ResetLocalScale(InteractionHost);
            ApplyLocalMotion();
        }
        else
        {
            ApplyLocalMotion();
        }
    }

    /// <summary>
    /// Takes the hover preview back after something else was driving the icon — a drag, which turns
    /// <see cref="UsePointerMotion"/> off while it owns the pointer. Whether the pointer is still over the
    /// icon is this icon's own business, so it is decided here rather than by the caller.
    /// </summary>
    internal void ResumePointerMotion()
    {
        UsePointerMotion = true;
        ApplyLocalMotion();
    }

    private static void OnStateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((DockIcon)dependencyObject).UpdateState();

    private static void OnLabelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((DockIcon)dependencyObject).NameIcon();

    private static void OnPointerMotionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var icon = (DockIcon)dependencyObject;
        if ((bool)args.NewValue)
        {
            icon.ApplyLocalMotion();
        }
        else
        {
            // Something else is driving this icon now, so its own preview has to let go completely rather
            // than leave a scale behind for the new owner to inherit.
            DockIconMotion.ResetLocalScale(icon.InteractionHost);
        }
    }

    /// <summary>
    /// Draws the icon's own hover and press preview, or clears it. Skipped entirely while something else owns
    /// the pointer, because the owner's transform is the one that has to be visible.
    /// </summary>
    private void ApplyLocalMotion()
    {
        if (!UsePointerMotion)
        {
            return;
        }

        if (_nexusMotionActive)
        {
            DockIconMotion.SetDepth(
                this,
                IsPressedPreview
                    ? DockIconMotion.Depth.Pressed
                    : _pointerOver ? DockIconMotion.Depth.Raised : DockIconMotion.Depth.Resting);
            return;
        }

        var pressed = IsPressedPreview;
        DockIconMotion.SetLocalScale(InteractionHost, pressed ? PressScale : _pointerOver ? HoverScale : 1, lift: _pointerOver && !pressed);
        DockIconMotion.SetDepth(this, pressed
            ? DockIconMotion.Depth.Pressed
            : _pointerOver ? DockIconMotion.Depth.Raised : DockIconMotion.Depth.Resting);
    }

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

        if (active)
        {
            // Chosen: the one state that earns a surface, so a selected pin is legible over any wallpaper.
            IconSurface.Background = Brush("MuralisGlassMediumBrush");
            IconSurface.BorderBrush = Brush("MuralisBorderActiveBrush");
            IconEdgeHighlight.Opacity = 1;
        }
        else if (_pointerOver)
        {
            IconSurface.Background = Brush("MuralisSurfaceHighBrush");
            IconSurface.BorderBrush = Brush("MuralisBorderHoverBrush");
            IconEdgeHighlight.Opacity = 1;
        }
        else
        {
            // At rest the icon paints nothing at all. The artwork sits directly on the wallpaper, which is
            // what makes the dock read as floating icons rather than as a row of tiles on a plate.
            IconSurface.Background = null;
            IconSurface.BorderBrush = null;
            IconEdgeHighlight.Opacity = 0;
        }

        if (IsPressedPreview)
        {
            if (!_nexusMotionActive)
            {
                DockIconMotion.SetLocalScale(InteractionHost, PressScale, lift: false);
            }
        }
    }

    /// <summary>
    /// Names the magnified square for assistive tools: the pin's name when the dock has one, and otherwise
    /// the name the markup already gave it.
    /// </summary>
    private void NameIcon()
    {
        var label = Label;
        if (!string.IsNullOrWhiteSpace(label))
        {
            AutomationProperties.SetName(NexusMotionHost, label);
        }
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string token) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[token];
}
