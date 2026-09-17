using System.ComponentModel;
using System.Runtime.CompilerServices;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;

namespace Muralis.App.UI.Dock;

/// <summary>
/// One pinned application as the dock draws it. It holds the model record, the picture the shell gave
/// for it, and the two things that can be true of the pin rather than of the file: it is unavailable
/// because its target is gone, or it is highlighted because the user just tried to pin it again.
/// </summary>
/// <remarks>
/// The item is reused while its id stays in the list, so an icon that has already been read is not
/// read again when the pin moves or when a neighbour is added.
/// </remarks>
public sealed class PinnedAppViewItem : INotifyPropertyChanged
{
    private PinnedApp _app;
    private object _iconContent;
    private bool _isAvailable;
    private bool _isHighlighted;
    private bool _iconLoaded;
    private string? _warning;

    public PinnedAppViewItem(PinnedApp app, bool isAvailable, string? warning)
    {
        _app = app;
        _isAvailable = isAvailable;
        _warning = warning;
        _iconContent = ShellIconVisual.Fallback(FallbackGlyph(app.Kind));
    }

    public PinnedApp App => _app;

    public string Id => _app.Id;

    public string DisplayName => _app.DisplayName;

    /// <summary>Where this pin starts. Shown to the user when the pin cannot be started.</summary>
    public string LaunchTarget => _app.LaunchTarget;

    public object IconContent
    {
        get => _iconContent;
        private set => Set(ref _iconContent, value);
    }

    /// <summary>False once the file behind the pin has gone. The pin stays; only its appearance changes.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (Set(ref _isAvailable, value))
            {
                Raise(nameof(IsUnavailable));
            }
        }
    }

    /// <summary>The same fact as <see cref="IsAvailable"/>, the way XAML needs it without a converter.</summary>
    public bool IsUnavailable => !_isAvailable;

    /// <summary>Set briefly on the pin the user already has, after a repeated add.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }

    /// <summary>Why the pin cannot be used, or null when there is nothing to warn about.</summary>
    public string? Warning
    {
        get => _warning;
        private set
        {
            if (Set(ref _warning, value))
            {
                Raise(nameof(Tooltip));
            }
        }
    }

    /// <summary>What the pointer shows: the name, plus why the pin is unusable when it is.</summary>
    public string Tooltip => _warning ?? DisplayName;

    /// <summary>Whether the shell picture has already been read for this pin.</summary>
    public bool HasIcon => _iconLoaded;

    /// <summary>
    /// Re-points the item at the same pin after the list was written. The icon is left alone: the
    /// launch target of an existing pin does not change, so re-reading it would only flicker.
    /// </summary>
    public void Update(PinnedApp app, bool isAvailable, string? warning)
    {
        var renamed = !string.Equals(_app.DisplayName, app.DisplayName, StringComparison.Ordinal);
        _app = app;
        if (renamed)
        {
            Raise(nameof(DisplayName));
            Raise(nameof(LaunchTarget));
        }

        IsAvailable = isAvailable;
        Warning = warning;
        Raise(nameof(Tooltip));
    }

    /// <summary>
    /// Reads the pin's picture from the shell, once. Called on the UI thread, and never on a path the
    /// user is waiting for: the pin is drawn with its fallback glyph first and the real icon replaces
    /// it when it arrives.
    /// </summary>
    public async Task LoadIconAsync(IShellIconProvider icons, CancellationToken cancellationToken = default)
    {
        if (_iconLoaded)
        {
            return;
        }

        _iconLoaded = true;
        var visual = await ShellIconVisual.CreateAsync(icons, _app.IconIdentity, cancellationToken);
        if (visual is not null)
        {
            IconContent = visual;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FallbackGlyph(PinnedAppKind kind) => kind switch
    {
        PinnedAppKind.Shortcut => "\uE71B",
        _ => "\uE7FC",
    };
}
