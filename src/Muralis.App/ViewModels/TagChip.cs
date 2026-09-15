using CommunityToolkit.Mvvm.Input;

namespace Muralis.App.ViewModels;

/// <summary>
/// A chip in the Detail page's tag list. Carries its own remove command so the item template
/// can bind without reaching back into the page's view model.
/// </summary>
public sealed class TagChip
{
    public TagChip(string name, string removeLabel, Action<string> remove)
    {
        Name = name;
        RemoveLabel = removeLabel;
        RemoveCommand = new RelayCommand(() => remove(name));
    }

    public string Name { get; }

    /// <summary>Localized accessibility name for the chip's remove button.</summary>
    public string RemoveLabel { get; }

    public IRelayCommand RemoveCommand { get; }
}

/// <summary>An entry in the Library's tag filter: a tag value, or "all tags" (<see cref="Tag"/> is <c>null</c>).</summary>
public sealed class TagFilterOption
{
    public TagFilterOption(string? tag, string name)
    {
        Tag = tag;
        Name = name;
    }

    public string? Tag { get; }

    public string Name { get; }
}
