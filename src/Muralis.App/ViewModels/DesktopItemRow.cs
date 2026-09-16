using CommunityToolkit.Mvvm.Input;
using Muralis.Core.Desktop;

namespace Muralis.App.ViewModels;

/// <summary>
/// One line of the desktop canvas item list: the item as the user will see it — its name, what it
/// opens and whether that target is still there. Data only, plus the one action a row offers, and
/// missing is decided once per list refresh, off the page's thread.
/// </summary>
public sealed class DesktopItemRow
{
    public DesktopItemRow(DesktopItem item, bool isMissing, Func<DesktopItemRow, Task> remove)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(remove);
        Item = item;
        IsMissing = isMissing;

        // Each row carries its own command: a data template cannot reach the page's view model.
        RemoveCommand = new AsyncRelayCommand(() => remove(this));
    }

    public DesktopItem Item { get; }

    public string Id => Item.Id;

    public string Name => Item.Name;

    /// <summary>What the item opens, shown under its name so two items with one name stay apart.</summary>
    public string Location => Item.Location;

    /// <summary>Whether the target was gone the last time the list was read.</summary>
    public bool IsMissing { get; }

    /// <summary>The glyph standing for the kind of target this item has.</summary>
    public string Glyph => Item.Target switch
    {
        ShortcutTarget => "\uE71B",
        FolderTarget => "\uE8B7",
        UrlTarget => "\uE774",
        FileTarget => "\uE8A5",
        ApplicationTarget => "\uE71D",
        _ => "\uE8A5",
    };

    /// <summary>Takes this item off the canvas; the real file, shortcut or folder is left alone.</summary>
    public IAsyncRelayCommand RemoveCommand { get; }

    /// <summary>
    /// Stable id for the remove button, one per item, so the UI tests can address a row without
    /// depending on the language the app is running in.
    /// </summary>
    public string RemoveAutomationId => $"CanvasRemoveItem_{Id}";
}
