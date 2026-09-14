using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IFilePickerService _filePicker;
    private readonly ILocalLibrary _library;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? SuccessNotice { get; set; }

    [ObservableProperty]
    public partial string? ErrorNotice { get; set; }

    public LibraryViewModel(
        INavigationService navigation,
        IFilePickerService filePicker,
        ILocalLibrary library,
        ILogger<LibraryViewModel> logger)
    {
        _navigation = navigation;
        _filePicker = filePicker;
        _library = library;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _library.Changed += OnLibraryChanged;
        RefreshItems();
    }

    public ObservableCollection<Wallpaper> Items { get; } = [];

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    [RelayCommand]
    private async Task ImportAsync()
    {
        _logger.LogDebug("Import requested by the user");
        var files = await _filePicker.PickImageFilesAsync();
        _logger.LogDebug("File picker returned {Count} files", files.Count);
        if (files.Count == 0)
        {
            return;
        }

        IsLoading = true;
        SuccessNotice = null;
        ErrorNotice = null;

        try
        {
            var result = await _library.ImportAsync(files);
            RefreshItems();

            var parts = new List<string>();
            if (result.Added > 0)
            {
                parts.Add(result.Added == 1 ? "1 wallpaper added" : $"{result.Added} wallpapers added");
            }

            if (result.Duplicates > 0)
            {
                parts.Add($"{result.Duplicates} already in your library");
            }

            if (result.Failed > 0)
            {
                parts.Add($"{result.Failed} could not be read");
            }

            if (result.Added == 0 && result.Failed > 0)
            {
                ErrorNotice = string.Join(", ", parts) + ".";
            }
            else if (parts.Count > 0)
            {
                SuccessNotice = string.Join(", ", parts) + ".";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed");
            ErrorNotice = "The import failed. See the log for details.";
        }
        finally
        {
            IsLoading = false;
        }
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

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        // The event can fire from a background continuation; marshal to the UI thread.
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
        foreach (var item in _library.Items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
