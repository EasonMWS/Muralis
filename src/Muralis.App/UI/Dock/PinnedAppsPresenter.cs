using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;

namespace Muralis.App.UI.Dock;

/// <summary>
/// Draws the pinned applications for one dock surface. It reads the list from
/// <see cref="IPinnedAppService"/>, which is the only copy that is saved, and mirrors it into view
/// items the dock binds to; it never writes the order itself, so a drag that is abandoned cannot
/// leave a saved order behind.
/// </summary>
/// <remarks>
/// One presenter per surface rather than a shared singleton: the bottom Shelf lives in its own window
/// with its own UI thread, and a collection can only be bound from the thread that made it. Each
/// presenter subscribes to the same service, so the surfaces still cannot disagree.
/// </remarks>
public sealed class PinnedAppsPresenter
{
    private static readonly TimeSpan HighlightDuration = TimeSpan.FromMilliseconds(1600);

    private readonly IPinnedAppService _service;
    private readonly IShellIconProvider _icons;
    private readonly IApplicationLocationRevealer _revealer;
    private readonly IFilePickerService _picker;
    private readonly ILocalizationService _localization;
    private readonly ILogger<PinnedAppsPresenter> _logger;
    private readonly DispatcherQueue _dispatcher;
    private bool _subscribed;
    private bool _restored;

    public PinnedAppsPresenter(
        IPinnedAppService service,
        IShellIconProvider icons,
        IApplicationLocationRevealer revealer,
        IFilePickerService picker,
        ILocalizationService localization,
        ILogger<PinnedAppsPresenter> logger,
        DispatcherQueue dispatcher)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _revealer = revealer ?? throw new ArgumentNullException(nameof(revealer));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>The pins, in dock order. Bound directly by the dock.</summary>
    public ObservableCollection<PinnedAppViewItem> Items { get; } = [];

    /// <summary>Raised on the UI thread once the view items match the saved list again.</summary>
    public event EventHandler? Projected;

    public int MaximumCount => _service.MaximumCount;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>Whether the zone has no room for another pin.</summary>
    public bool IsFull => Items.Count >= _service.MaximumCount;

    /// <summary>Starts following the service, and adopts a list another surface already restored.</summary>
    public void Attach()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _service.Changed += OnServiceChanged;
        if (_service.Items.Count > 0)
        {
            Project(_service.Items);
        }
    }

    public void Detach()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _service.Changed -= OnServiceChanged;
    }

    /// <summary>
    /// Reads the saved pins and draws them. Data first, pictures after: the dock is on screen before
    /// the shell has been asked for a single icon.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_restored)
        {
            Project(_service.Items);
            return;
        }

        _restored = true;
        Project(await _service.RestoreAsync(cancellationToken).ConfigureAwait(true));
    }

    /// <summary>
    /// The whole add gesture, from the click to the line the user reads. Both dock surfaces go through
    /// it, so an app can only be pinned one way and can only ever be refused for one reason.
    /// </summary>
    public async Task<string?> AddFromPickerAsync(CancellationToken cancellationToken = default)
    {
        if (IsFull)
        {
            return _localization.Format("Dock_Pinned_Full", MaximumCount);
        }

        var path = await _picker.PickApplicationFileAsync();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var result = await AddAsync(path, cancellationToken);
        return DescribeRefusal(result)
            ?? _localization.Format("Dock_Pinned_Added", result.Item?.DisplayName ?? Path.GetFileName(path));
    }

    /// <summary>
    /// Pins the application at <paramref name="path"/>. A duplicate is not an error: the pin the user
    /// already has is highlighted so the answer to "I pinned it again" is visible rather than spoken.
    /// </summary>
    public async Task<PinnedAppAddResult> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await _service.AddAsync(path, cancellationToken).ConfigureAwait(true);
        if (result.Outcome == PinnedAppAddOutcome.Duplicate && result.ExistingId is not null)
        {
            Highlight(result.ExistingId);
        }

        return result;
    }

    /// <summary>
    /// Unpins the application and hands back the line to show the user, or null when the id was
    /// already gone. The pin is the only thing removed; the file it pointed at is never touched.
    /// </summary>
    public async Task<string?> UnpinAsync(string id, CancellationToken cancellationToken = default)
    {
        var item = Find(id);
        if (item is null || !await _service.RemoveAsync(id, cancellationToken).ConfigureAwait(true))
        {
            return null;
        }

        return _localization.Format("Dock_Pinned_Unpinned", item.DisplayName);
    }

    /// <summary>
    /// One line the user can read when an add did not happen, or null when it did. A repeated add is
    /// worth saying out loud as well as highlighting, because the pin it refers to may be scrolled out
    /// of sight in another surface.
    /// </summary>
    public string? DescribeRefusal(PinnedAppAddResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            PinnedAppAddOutcome.Added => null,
            PinnedAppAddOutcome.Duplicate => _localization.Format(
                "Dock_Pinned_AlreadyPinned",
                Find(result.ExistingId ?? string.Empty)?.DisplayName ?? string.Empty),
            _ => result.Error,
        };
    }

    /// <summary>
    /// Commits a drag. Called once, on release: the items the user carried around while the pointer
    /// was down were only ever drawn differently, never saved.
    /// </summary>
    public Task MoveAsync(string id, int targetIndex, CancellationToken cancellationToken = default) =>
        _service.MoveAsync(id, targetIndex, cancellationToken);

    public Task<ApplicationLaunchResult> LaunchAsync(string id, CancellationToken cancellationToken = default) =>
        _service.LaunchAsync(id, cancellationToken);

    /// <summary>Opens the folder the pin's file lives in, without starting anything.</summary>
    public async Task<bool> RevealAsync(string id, CancellationToken cancellationToken = default)
    {
        var item = Find(id);
        return item is not null && await _revealer.RevealAsync(item.LaunchTarget, cancellationToken).ConfigureAwait(true);
    }

    public PinnedAppViewItem? Find(string id) =>
        Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Marks a pin as the thing the user was just told about, briefly.</summary>
    public void Highlight(string id)
    {
        var item = Find(id);
        if (item is null)
        {
            return;
        }

        item.IsHighlighted = true;
        _ = ClearHighlightLaterAsync(item);
    }

    /// <summary>Rebuilds the view items around the saved list, reusing the ones that are still there.</summary>
    private void Project(IReadOnlyList<PinnedApp> apps)
    {
        for (var position = 0; position < apps.Count; position++)
        {
            var app = apps[position];
            var existing = IndexOf(app.Id, position);
            if (existing < 0)
            {
                Items.Insert(position, Create(app));
                continue;
            }

            if (existing > position)
            {
                Items.Move(existing, position);
            }

            Apply(Items[position], app);
        }

        while (Items.Count > apps.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        foreach (var item in Items)
        {
            if (!item.HasIcon)
            {
                _ = LoadIconAsync(item);
            }
        }

        Projected?.Invoke(this, EventArgs.Empty);
    }

    private PinnedAppViewItem Create(PinnedApp app)
    {
        var available = _service.IsAvailable(app);
        return new PinnedAppViewItem(app, available, available ? null : MissingWarning(app));
    }

    private void Apply(PinnedAppViewItem item, PinnedApp app)
    {
        var available = _service.IsAvailable(app);
        item.Update(app, available, available ? null : MissingWarning(app));
    }

    private string MissingWarning(PinnedApp app) =>
        _localization.Format("Dock_Pinned_Missing", app.DisplayName, app.LaunchTarget);

    private async Task LoadIconAsync(PinnedAppViewItem item)
    {
        try
        {
            await item.LoadIconAsync(_icons);
        }
        catch (Exception ex)
        {
            // An icon the shell would not give up is not a reason to lose the pin; the fallback glyph
            // stays and the launch is unaffected.
            _logger.LogWarning(ex, "The icon for the pinned app {Name} could not be loaded", item.DisplayName);
        }
    }

    private async Task ClearHighlightLaterAsync(PinnedAppViewItem item)
    {
        await Task.Delay(HighlightDuration);
        if (_dispatcher.HasThreadAccess)
        {
            item.IsHighlighted = false;
        }
        else
        {
            _dispatcher.TryEnqueue(() => item.IsHighlighted = false);
        }
    }

    private int IndexOf(string id, int from)
    {
        for (var i = from; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void OnServiceChanged(object? sender, IReadOnlyList<PinnedApp> apps)
    {
        if (_dispatcher.HasThreadAccess)
        {
            Project(apps);
        }
        else
        {
            _dispatcher.TryEnqueue(() => Project(apps));
        }
    }
}
