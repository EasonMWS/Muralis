using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Muralis.App.UI.Controls;

public class MuralisNavigationItem : Button
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected),
        typeof(bool),
        typeof(MuralisNavigationItem),
        new PropertyMetadata(false, OnIsSelectedChanged));

    public MuralisNavigationItem() => Loaded += (_, _) => UpdateSelection(false);

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateSelection(false);
    }

    private static void OnIsSelectedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((MuralisNavigationItem)dependencyObject).UpdateSelection(true);

    private void UpdateSelection(bool transitions) =>
        VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", transitions);
}
