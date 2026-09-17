using System.ComponentModel;
using System.Runtime.CompilerServices;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;

namespace Muralis.App.UI.Dock;

public sealed class DesktopShelfViewItem : INotifyPropertyChanged
{
    private object _iconContent;
    private bool _isSelected;

    public DesktopShelfViewItem(DesktopShelfItem item)
    {
        Item = item;
        _iconContent = ShellIconVisual.Fallback(FallbackGlyph(item.ItemType));
    }

    public DesktopShelfItem Item { get; }

    public string DisplayName => Item.DisplayName;

    public object IconContent
    {
        get => _iconContent;
        private set => Set(ref _iconContent, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public async Task LoadIconAsync(IShellIconProvider icons, CancellationToken cancellationToken = default)
    {
        var visual = await ShellIconVisual.CreateAsync(icons, Item.IconKey, cancellationToken);
        if (visual is not null)
        {
            IconContent = visual;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string FallbackGlyph(DockShellItemType type) => type switch
    {
        DockShellItemType.Folder => "\uE8B7",
        DockShellItemType.Shortcut or DockShellItemType.Application => "\uE71B",
        _ => "\uE8A5",
    };
}
