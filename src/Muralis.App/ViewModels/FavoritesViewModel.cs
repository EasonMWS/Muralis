using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class FavoritesViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly ILocalLibrary _library;
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public FavoritesViewModel(
        INavigationService navigation,
        ILocalLibrary library,
        ILocalizationService localization)
        : base(localization)
    {
        _navigation = navigation;
        _library = library;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _library.Changed += OnLibraryChanged;
        RefreshItems();
    }

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

    public override void DetachFromPage()
    {
        _library.Changed -= OnLibraryChanged;
        base.DetachFromPage();
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            RefreshItems();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(RefreshItems);
        }
    }

    private void RefreshItems()
    {
        Items.Clear();
        foreach (var item in _library.Favorites)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
