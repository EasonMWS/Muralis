using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muralis.App.Services;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class FavoritesViewModel : ObservableObject
{
    private readonly INavigationService _navigation;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public FavoritesViewModel(INavigationService navigation) => _navigation = navigation;

    public ObservableCollection<Wallpaper> Items { get; } = [];

    public bool IsEmpty => !IsLoading && Items.Count == 0;

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
