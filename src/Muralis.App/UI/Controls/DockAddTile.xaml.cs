using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Muralis.App.UI.Controls;

/// <summary>
/// The empty slot at the end of the Pinned Apps zone. It is the same size as a pinned icon so the
/// zone keeps its shape whether it holds nothing or is full, and it carries the zone's only hint:
/// pinning is done by adding an app, not by discovering a hidden menu.
/// </summary>
public sealed partial class DockAddTile : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(DockAddTile), new PropertyMetadata(string.Empty));

    public DockAddTile() => InitializeComponent();

    /// <summary>Raised when the user asks to pin something. Who opens the picker is the page's business.</summary>
    public event EventHandler? AddRequested;

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private void OnAddClicked(object sender, RoutedEventArgs args) =>
        AddRequested?.Invoke(this, EventArgs.Empty);
}
