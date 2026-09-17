using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.UI.Motion;
using System.Numerics;

namespace Muralis.App.UI.Controls;

public class MuralisButton : Button
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant),
        typeof(MuralisButtonVariant),
        typeof(MuralisButton),
        new PropertyMetadata(MuralisButtonVariant.Primary, OnVariantChanged));

    private bool _pointerOver;

    public MuralisButton()
    {
        Loaded += (_, _) =>
        {
            ApplyVariant();
            SetDepth(4);
        };
        PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            HoverMotion.Enter(this, lift: false);
            SetDepth(9);
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            HoverMotion.Exit(this);
            SetDepth(4);
        };
        PointerPressed += (_, _) =>
        {
            PressMotion.Down(this);
            SetDepth(2);
        };
        PointerReleased += (_, _) =>
        {
            PressMotion.Release(this, _pointerOver);
            SetDepth(_pointerOver ? 9 : 4);
        };
        PointerCanceled += (_, _) =>
        {
            HoverMotion.Exit(this);
            SetDepth(4);
        };
    }

    public MuralisButtonVariant Variant
    {
        get => (MuralisButtonVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    private static void OnVariantChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((MuralisButton)dependencyObject).ApplyVariant();

    private void ApplyVariant()
    {
        if (Application.Current is null)
        {
            return;
        }

        var key = Variant switch
        {
            MuralisButtonVariant.Secondary => "MuralisSecondaryButtonStyle",
            MuralisButtonVariant.Ghost => "MuralisGhostButtonStyle",
            _ => "MuralisPrimaryButtonStyle",
        };

        if (Application.Current.Resources[key] is Style style)
        {
            Style = style;
        }
    }

    private void SetDepth(float z) => Translation = new Vector3(Translation.X, Translation.Y, z);
}
