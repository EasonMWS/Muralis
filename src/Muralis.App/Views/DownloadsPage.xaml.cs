using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.ViewModels;

namespace Muralis.App.Views;

public sealed partial class DownloadsPage : Page
{
    public DownloadsPage()
    {
        ViewModel = App.GetService<DownloadsViewModel>();
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public DownloadsViewModel ViewModel { get; }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.DetachFromPage();
}
