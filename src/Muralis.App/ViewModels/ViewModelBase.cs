using CommunityToolkit.Mvvm.ComponentModel;
using Muralis.App.Services;

namespace Muralis.App.ViewModels;

/// <summary>
/// Base class for view models that show localized text. Subscribes to language changes
/// (weakly, through <see cref="ILocalizationService.Register"/>) so transient view models
/// do not leak.
/// </summary>
public abstract class ViewModelBase : ObservableObject, ILanguageAware
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
}
