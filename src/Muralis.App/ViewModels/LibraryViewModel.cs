using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muralis.App.Services;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public LibraryViewModel(INavigationService navigation, IDialogService dialogs)
    {
        _navigation = navigation;
        _dialogs = dialogs;
    }

    public ObservableCollection<Wallpaper> Items { get; } = [];

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    [RelayCommand]
    private async Task ImportAsync()
    {
        await _dialogs.ShowMessageAsync(
            "Import your own images",
            "Importing local images arrives in the next milestone (M2). Until then, explore the sample wallpapers on the Browse page.");
    }

    [RelayCommand]
    private void OpenBrowse() => _navigation.NavigateTo(Routes.Browse);

    [RelayCommand]
    private void OpenWallpaper(Wallpaper? wallpaper)
    {
        if (wallpaper is not null)
        {
            _navigation.NavigateTo(Routes.Detail, wallpaper);
        }
    }
}
