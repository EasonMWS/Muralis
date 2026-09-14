using CommunityToolkit.Mvvm.ComponentModel;
using Muralis.App.Services;
using Muralis.Core.Abstractions;

namespace Muralis.App.ViewModels;

/// <summary>
/// Base class for view models that show localized text or source lists. Subscribes to
/// language and provider changes (weakly, through the core services' <c>Register</c>
/// methods) so transient view models do not leak.
/// </summary>
public abstract class ViewModelBase : ObservableObject, ILanguageAware, IProviderManagerObserver
{
    private readonly CancellationTokenSource _pageCancellation = new();

    protected ViewModelBase(ILocalizationService localization)
    {
        Loc = localization;
        localization.Register(this);
    }

    protected ILocalizationService Loc { get; }

    /// <summary>
    /// Cancelled when the page that owns this view model is left, so provider calls and
    /// thumbnail loading stop instead of finishing work nobody will see.
    /// </summary>
    protected CancellationToken PageToken => _pageCancellation.Token;

    /// <summary>Raise change notifications for localized properties and refresh transient text.</summary>
    public virtual void OnLanguageChanged()
    {
    }

    /// <summary>Called when the user enables/disables sources or changes the default source.</summary>
    public virtual void OnProvidersChanged()
    {
    }

    /// <summary>Called by the page from its Unloaded handler (navigated away or closed).</summary>
    public void DetachFromPage()
    {
        if (!_pageCancellation.IsCancellationRequested)
        {
            _pageCancellation.Cancel();
        }
    }
}
