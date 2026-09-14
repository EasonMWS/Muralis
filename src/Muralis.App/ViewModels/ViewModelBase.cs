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
    protected ViewModelBase(ILocalizationService localization)
    {
        Loc = localization;
        localization.Register(this);
    }

    protected ILocalizationService Loc { get; }

    /// <summary>Raise change notifications for localized properties and refresh transient text.</summary>
    public virtual void OnLanguageChanged()
    {
    }

    /// <summary>Called when the user enables/disables sources or changes the default source.</summary>
    public virtual void OnProvidersChanged()
    {
    }
}
