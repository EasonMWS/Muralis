using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muralis.App.UI.Controls;
using System.Numerics;

namespace Muralis.App.UI.Materials;

public class GlassSurface : ContentControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(GlassLevel),
        typeof(GlassSurface),
        new PropertyMetadata(GlassLevel.Medium, OnLevelChanged));
    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush),
        typeof(Brush),
        typeof(GlassSurface),
        new PropertyMetadata(null));

    public GlassSurface() => Loaded += (_, _) => ApplyLevel();

    public GlassLevel Level
    {
        get => (GlassLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private static void OnLevelChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((GlassSurface)dependencyObject).ApplyLevel();

    private void ApplyLevel()
    {
        if (Application.Current is null)
        {
            return;
        }

        var key = Level switch
        {
            GlassLevel.Low => "MuralisGlassLowStyle",
            GlassLevel.High => "MuralisGlassHighStyle",
            _ => "MuralisGlassMediumStyle",
        };

        if (Application.Current.Resources[key] is Style style)
        {
            Style = style;
        }

        // Translation is a fast rendering property (not a dependency property), so
        // it is applied here instead of through a Style setter.
        Translation = new Vector3(0, 0, Level switch
        {
            GlassLevel.Low => 8,
            GlassLevel.High => 24,
            _ => 16,
        });
    }
}
