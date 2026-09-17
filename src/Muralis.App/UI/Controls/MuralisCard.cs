using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Muralis.App.UI.Motion;
using System.Windows.Input;
using System.Numerics;
using Windows.System;

namespace Muralis.App.UI.Controls;

public class MuralisCard : ContentControl
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant),
        typeof(MuralisCardVariant),
        typeof(MuralisCard),
        new PropertyMetadata(MuralisCardVariant.Default, OnVariantChanged));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(MuralisCard), new PropertyMetadata(null));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter), typeof(object), typeof(MuralisCard), new PropertyMetadata(null));

    private bool _pointerOver;

    public MuralisCard()
    {
        Loaded += (_, _) =>
        {
            UpdateInteractionState();
            UpdateVisualState(false);
            SetDepth(Variant == MuralisCardVariant.Default ? 8 : 12);
        };
        Tapped += (_, _) => Invoke();
        KeyDown += OnKeyDown;
        PointerEntered += (_, _) =>
        {
            if (Variant == MuralisCardVariant.Default)
            {
                return;
            }

            _pointerOver = true;
            VisualStateManager.GoToState(this, "PointerOver", true);
            HoverMotion.Enter(this);
            SetDepth(22);
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            UpdateVisualState(true);
            HoverMotion.Exit(this);
            SetDepth(Variant == MuralisCardVariant.Default ? 8 : 12);
        };
        PointerPressed += (_, _) =>
        {
            if (Variant != MuralisCardVariant.Default)
            {
                VisualStateManager.GoToState(this, "Pressed", true);
                PressMotion.Down(this);
                SetDepth(6);
            }
        };
        PointerReleased += (_, _) =>
        {
            UpdateVisualState(true);
            PressMotion.Release(this, _pointerOver, lift: true);
            SetDepth(_pointerOver ? 22 : 12);
        };
    }

    public MuralisCardVariant Variant
    {
        get => (MuralisCardVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public event EventHandler? Invoked;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateVisualState(false);
    }

    private static void OnVariantChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var card = (MuralisCard)dependencyObject;
        card.UpdateInteractionState();
        card.UpdateVisualState(true);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            args.Handled = true;
            Invoke();
        }
    }

    private void Invoke()
    {
        if (!IsEnabled || Variant == MuralisCardVariant.Default)
        {
            return;
        }

        if (Command?.CanExecute(CommandParameter) == true)
        {
            Command.Execute(CommandParameter);
        }

        Invoked?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateInteractionState() => IsTabStop = Variant != MuralisCardVariant.Default;

    private void UpdateVisualState(bool transitions)
    {
        var state = Variant == MuralisCardVariant.Selected
            ? "Selected"
            : _pointerOver && Variant == MuralisCardVariant.Interactive ? "PointerOver" : "Normal";
        VisualStateManager.GoToState(this, state, transitions);
    }

    private void SetDepth(float z) => Translation = new Vector3(Translation.X, Translation.Y, z);
}
