using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Models;

namespace Muralis.App.ViewModels;

public sealed partial class LibraryViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly IFilePickerService _filePicker;
    private readonly ILocalLibrary _library;
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private (string Key, object?[] Args)? _notice;

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
        ILocalizationService localization,
        ILogger<LibraryViewModel> logger)
        : base(localization)
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

    public override void OnLanguageChanged()
    {
        if (_notice is not { } notice)
        {
            // Pre-formatted notices cannot be re-translated; drop them rather than
            // leaving text in the previous language on screen.
            SuccessNotice = null;
            ErrorNotice = null;
            return;
        }

        var message = Loc.Format(notice.Key, notice.Args);
        if (ErrorNotice is not null)
        {
            ErrorNotice = message;
        }
        else
        {
            SuccessNotice = message;
        }
    }

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
        ClearNotices();

        try
        {
            var result = await _library.ImportAsync(files);
            RefreshItems();

            var parts = new List<string>();
            if (result.Added > 0)
            {
                parts.Add(result.Added == 1
                    ? Loc.Get("Library_Import_OneAdded")
                    : Loc.Format("Library_Import_ManyAdded", result.Added));
            }

            if (result.Duplicates > 0)
            {
                parts.Add(Loc.Format("Library_Import_Duplicates", result.Duplicates));
            }

            if (result.Failed > 0)
            {
                parts.Add(Loc.Format("Library_Import_Failed", result.Failed));
            }

            if (parts.Count == 0)
            {
                return;
            }

            var summary = string.Join(Loc.Get("Common_ListSeparator"), parts) + Loc.Get("Common_Notice_Suffix");
            if (result.Added == 0 && result.Failed > 0)
            {
                ErrorNotice = summary;
            }
            else
            {
                SuccessNotice = summary;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed");
            SetError("Library_Error_ImportFailed");
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

    private void SetError(string key)
    {
        _notice = (key, []);
        SuccessNotice = null;
        ErrorNotice = Loc.Get(key);
    }

    private void ClearNotices()
    {
        _notice = null;
        SuccessNotice = null;
        ErrorNotice = null;
    }

    public override void DetachFromPage()
    {
        _library.Changed -= OnLibraryChanged;
        base.DetachFromPage();
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
