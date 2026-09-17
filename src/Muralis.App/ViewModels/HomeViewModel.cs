using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Models;
using Muralis.Core.Services;

namespace Muralis.App.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly WallpaperProviderManager _providers;
    private readonly IImageCacheService _imageCache;
    private readonly ILocalLibrary _library;
    private readonly INavigationService _navigation;
    private readonly MuralisModeHero _hero;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private string? _errorKey;
    private object?[] _errorArgs = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial Wallpaper? Featured { get; set; }

    public HomeViewModel(
        WallpaperProviderManager providers,
        IImageCacheService imageCache,
        ILocalLibrary library,
        INavigationService navigation,
        MuralisModeHero hero,
        ILocalizationService localization,
        ILogger<HomeViewModel> logger)
        : base(localization)
    {
        _providers = providers;
        _imageCache = imageCache;
        _library = library;
        _navigation = navigation;
        _hero = hero;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _library.Changed += OnLibraryChanged;
        _hero.StateChanged += OnHeroChanged;
    }

    public ObservableCollection<Wallpaper> Recommended { get; } = [];

    public ObservableCollection<Wallpaper> RecentlyUsed { get; } = [];

    public bool HasRecent => RecentlyUsed.Count > 0;

    public string Greeting => Loc.Get(DateTime.Now.Hour switch
    {
        < 5 => "Greeting_Night",
        < 12 => "Greeting_Morning",
        < 18 => "Greeting_Afternoon",
        _ => "Greeting_Evening",
    });

    public bool IsInitialLoading => IsLoading && Recommended.Count == 0;

    /// <summary>
    /// The Muralis Mode hero. Every property below is read from the desktop's own state each time it is
    /// asked for, so the hero cannot hold a mode that the settings page would disagree with.
    /// </summary>
    public bool IsHeroReady => _hero.State == MuralisModeHeroState.Ready;

    public bool IsHeroActive => _hero.State == MuralisModeHeroState.Active;

    public bool IsHeroFailed => _hero.State == MuralisModeHeroState.Error;

    /// <summary>True while the desktop is being rearranged, whichever way the change is going.</summary>
    public bool IsHeroBusy => _hero.State is MuralisModeHeroState.Entering or MuralisModeHeroState.Exiting;

    /// <summary>Entering is offered while the desktop is native, a failed attempt included.</summary>
    public bool CanEnterMuralisMode => IsHeroReady || IsHeroFailed;

    /// <summary>Neither action may be pressed twice over: the desktop is asked for one mode at a time.</summary>
    public bool CanUseMuralisModeActions => !_hero.IsBusy;

    public string HeroTitle => Loc.Get("Home_MuralisMode_Title");

    public string HeroTagline => Loc.Get("Home_MuralisMode_Tagline");

    public string HeroStatus => Loc.Get(_hero.State switch
    {
        MuralisModeHeroState.Entering => "Home_MuralisMode_Entering",
        MuralisModeHeroState.Exiting => "Home_MuralisMode_Exiting",
        MuralisModeHeroState.Active => "Home_MuralisMode_Active",
        MuralisModeHeroState.Error => "Home_MuralisMode_Error",
        _ => "Home_MuralisMode_Ready",
    });

    /// <summary>Enter, or Try Again after an attempt that did not take.</summary>
    public string HeroPrimaryText => Loc.Get(IsHeroFailed ? "Home_MuralisMode_TryAgain" : "Home_MuralisMode_Enter");

    public string HeroCustomizeText => Loc.Get("Home_MuralisMode_Customize");

    public string HeroExitText => Loc.Get("Home_MuralisMode_Exit");

    /// <summary>The desktop's own words for what went wrong, kept beside the translated headline.</summary>
    public string? HeroErrorDetail => IsHeroFailed ? _hero.Error : null;

    public string HeroDockLabel => Loc.Get("Home_MuralisMode_Dock");

    public string HeroShelfLabel => Loc.Get("Home_MuralisMode_Shelf");

    public string HeroMotionLabel => Loc.Get("Home_MuralisMode_Motion");

    public string HeroWidgetsLabel => Loc.Get("Home_MuralisMode_Widgets");

    /// <summary>
    /// The dock is a required part of Muralis Mode, so while the mode is on it is up. The Shelf is asked
    /// only to be ready: counting its items would tie the hero to a lifecycle of its own, and a number
    /// is not what the hero is for.
    /// </summary>
    public string? HeroDockStatus => IsHeroActive ? Loc.Get("Home_MuralisMode_Dock_Active") : null;

    public string? HeroShelfStatus => IsHeroActive ? Loc.Get("Home_MuralisMode_Shelf_Ready") : null;

    public string HeroMotionStatus => Loc.Get("Home_MuralisMode_ComingSoon");

    public string HeroWidgetsStatus => Loc.Get("Home_MuralisMode_ComingSoon");

    public override void OnLanguageChanged()
    {
        if (_errorKey is not null)
        {
            ErrorMessage = Loc.Format(_errorKey, _errorArgs);
        }

        OnPropertyChanged(nameof(Greeting));
        RaiseHeroTextChanged();
        RaiseHeroLabelsChanged();
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsInitialLoading));

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        SetError(null);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, PageToken);
        var linkedToken = linkedCancellation.Token;

        try
        {
            var result = await _providers
                .GetFeaturedAsync(24, linkedToken)
                .ConfigureAwait(true);

            if (result.Items.Count == 0)
            {
                _logger.LogWarning("No source could provide a featured feed");
                SetError("Home_Error_LoadFailed");
            }
            else if (result.Failures.Count > 0)
            {
                // The default source failed and the samples filled in.
                SetError("Home_Error_SourceUnavailable", ProviderDisplay.Name(Loc, result.Failures[0].Provider));
            }

            Recommended.Clear();
            foreach (var item in result.Items)
            {
                Recommended.Add(item);
            }

            Featured = Recommended.FirstOrDefault();
            await RefreshRecentAsync();

            _logger.LogInformation("Home feed loaded with {Count} wallpapers", Recommended.Count);

            // Pull thumbnails in the background so the hero and cards fill in as files arrive.
            // The page token stops that work when the user navigates away.
            _ = _imageCache.WarmThumbnailsAsync(Recommended.ToList(), PageToken);
        }
        catch (OperationCanceledException)
        {
            // The page was left while loading; nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the home feed");
            SetError("Home_Error_LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenWallpaper(Wallpaper? wallpaper)
    {
        if (wallpaper is not null)
        {
            _navigation.NavigateTo(Routes.Detail, wallpaper);
        }
    }

    [RelayCommand]
    private void OpenBrowse() => _navigation.NavigateTo(Routes.Browse);

    [RelayCommand]
    private void OpenFavorites() => _navigation.NavigateTo(Routes.Favorites);

    [RelayCommand]
    private void OpenLibrary() => _navigation.NavigateTo(Routes.Library);

    /// <summary>
    /// Enters Muralis Mode by asking the desktop for it. Everything the mode consists of — the hidden
    /// icons, the dock, the Shelf — is brought up by that one service, and this only reports the answer.
    /// </summary>
    [RelayCommand]
    private Task EnterMuralisModeAsync() => _hero.EnterAsync();

    [RelayCommand]
    private Task ExitMuralisModeAsync() => _hero.ExitAsync();

    /// <summary>The hero's Customize is the settings page: the experience is entered here, configured there.</summary>
    [RelayCommand]
    private void CustomizeMuralisMode() => _navigation.NavigateTo(Routes.Settings);

    /// <summary>Stores the resource key so the message can be re-resolved after a language change.</summary>
    private void SetError(string? key, params object?[] args)
    {
        _errorKey = key;
        _errorArgs = args;
        ErrorMessage = key is null ? null : Loc.Format(key, args);
    }

    public override void DetachFromPage()
    {
        _library.Changed -= OnLibraryChanged;
        _hero.StateChanged -= OnHeroChanged;
        base.DetachFromPage();
    }

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            _ = RefreshRecentAsync();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => _ = RefreshRecentAsync());
        }
    }

    /// <summary>
    /// The desktop answers on whichever thread it was working on, so the hero's state is carried back to
    /// the one the page reads it from.
    /// </summary>
    private void OnHeroChanged(object? sender, MuralisModeHeroState state)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            RaiseHeroChanged();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(RaiseHeroChanged);
        }
    }

    private void RaiseHeroChanged()
    {
        OnPropertyChanged(nameof(IsHeroReady));
        OnPropertyChanged(nameof(IsHeroActive));
        OnPropertyChanged(nameof(IsHeroFailed));
        OnPropertyChanged(nameof(IsHeroBusy));
        OnPropertyChanged(nameof(CanEnterMuralisMode));
        OnPropertyChanged(nameof(CanUseMuralisModeActions));
        RaiseHeroTextChanged();
    }

    private void RaiseHeroTextChanged()
    {
        OnPropertyChanged(nameof(HeroStatus));
        OnPropertyChanged(nameof(HeroPrimaryText));
        OnPropertyChanged(nameof(HeroErrorDetail));
        OnPropertyChanged(nameof(HeroDockStatus));
        OnPropertyChanged(nameof(HeroShelfStatus));
    }

    /// <summary>Every string the hero shows, for the one case the state alone does not cover: a new language.</summary>
    private void RaiseHeroLabelsChanged()
    {
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroTagline));
        OnPropertyChanged(nameof(HeroCustomizeText));
        OnPropertyChanged(nameof(HeroExitText));
        OnPropertyChanged(nameof(HeroDockLabel));
        OnPropertyChanged(nameof(HeroShelfLabel));
        OnPropertyChanged(nameof(HeroMotionLabel));
        OnPropertyChanged(nameof(HeroWidgetsLabel));
        OnPropertyChanged(nameof(HeroMotionStatus));
        OnPropertyChanged(nameof(HeroWidgetsStatus));
    }

    private async Task RefreshRecentAsync()
    {
        try
        {
            var recent = await _library.GetRecentlyUsedAsync(8);
            RecentlyUsed.Clear();
            foreach (var item in recent)
            {
                RecentlyUsed.Add(item);
            }

            OnPropertyChanged(nameof(HasRecent));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the recently used feed");
        }
    }
}
