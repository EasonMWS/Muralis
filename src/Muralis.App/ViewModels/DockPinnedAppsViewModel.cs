using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Muralis.App.Services;
using Muralis.App.UI.Dock;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;

namespace Muralis.App.ViewModels;

/// <summary>
/// The Dock's configuration surface: the apps pinned to it, in dock order, with the three things a user
/// can do to that list — add, remove, and reorder.
/// </summary>
/// <remarks>
/// <para>
/// <b>One source of truth.</b> This reads and writes the same <see cref="IPinnedAppService"/> the
/// desktop dock draws from, through the same <see cref="PinnedAppsPresenter"/> the dock uses. There is no
/// second pin store, no second ordering rule and no second icon path: an app added here is the app the
/// dock is already showing, and a reorder here is the order the dock is already drawn in.
/// </para>
/// <para>
/// Reordering is a pair of move-left / move-right actions rather than a drag. The commit is
/// <see cref="PinnedAppsPresenter.MoveAsync"/> either way, so the persisted order is identical to the one
/// the dock's own drag produces, and the surface does not need a second drag implementation to be useful.
/// </para>
/// </remarks>
public sealed class DockPinnedAppsViewModel : ViewModelBase
{
    private readonly IPinnedAppService _pinned;
    private readonly IFilePickerService _picker;
    private readonly IShellIconProvider _icons;
    private readonly ILogger<PinnedAppsPresenter> _pinnedLog;
    private readonly DispatcherQueue? _dispatcher;
    private readonly PinnedAppsPresenter _presenter;

    public DockPinnedAppsViewModel(
        IPinnedAppService pinned,
        IShellIconProvider icons,
        IApplicationLocationRevealer revealer,
        IFilePickerService picker,
        ILocalizationService localization,
        ILogger<PinnedAppsPresenter> pinnedLog)
        : base(localization)
    {
        _pinned = pinned ?? throw new ArgumentNullException(nameof(pinned));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _pinnedLog = pinnedLog ?? throw new ArgumentNullException(nameof(pinnedLog));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _presenter = new PinnedAppsPresenter(
            pinned,
            icons,
            revealer,
            picker,
            localization,
            pinnedLog,
            _dispatcher ?? DispatcherQueue.GetForCurrentThread());

        // Subscribed before the first attach, because attaching projects whatever the service already
        // holds and that first projection is what the section draws.
        _presenter.Projected += OnProjected;
        _presenter.Attach();
        _ = RestoreAsync();
    }

    /// <summary>The pinned applications, in dock order, as rows the section binds to.</summary>
    public ObservableCollection<DockPinRow> Rows { get; } = [];

    /// <summary>Whether there is nothing pinned, which is the state the dock hides itself in.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Whether the dock has no room for another pin.</summary>
    public bool IsFull => Rows.Count >= _pinned.MaximumCount;

    /// <summary>Raised with the key of the line to show the user, and whether it is bad news.</summary>
    public event EventHandler<(string Key, bool IsError)>? Reported;

    /// <summary>
    /// Reads the saved pins and draws them. Safe to call more than once: a second call re-reads whatever
    /// the service currently holds rather than going back to disk.
    /// </summary>
    public async Task RestoreAsync()
    {
        try
        {
            await _presenter.RestoreAsync();
        }
        catch (Exception ex)
        {
            // A list that cannot be read is a list the user can still add to; saying so is better than an
            // empty section that looks like it worked.
            _pinnedLog.LogError(ex, "The pinned applications could not be read for the Dock settings section");
            Reported?.Invoke(this, ("Dock_Pinned_Empty_State", true));
        }
    }

    /// <summary>Opens the picker and pins what the user chose, through the dock's own add path.</summary>
    public async Task AddAsync()
    {
        if (IsFull)
        {
            Reported?.Invoke(this, ("Dock_Pinned_Full", true));
            return;
        }

        try
        {
            var path = await _picker.PickApplicationFileAsync();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var result = await _presenter.AddAsync(path);
            if (_presenter.DescribeRefusal(result) is { } refusal)
            {
                Reported?.Invoke(this, (refusal, true));
                return;
            }

            Reported?.Invoke(this, ("Dock_Pinned_Added", false));
        }
        catch (Exception ex)
        {
            _pinnedLog.LogError(ex, "Pinning an application from the Dock settings section failed");
            Reported?.Invoke(this, ("Dock_Pinned_Full", true));
        }
    }

    /// <summary>Unpins an app. The file it points at is not touched.</summary>
    public async Task RemoveAsync(DockPinRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            if (await _presenter.UnpinAsync(row.Id) is { } notice)
            {
                Reported?.Invoke(this, (notice, false));
            }
        }
        catch (Exception ex)
        {
            _pinnedLog.LogError(ex, "Unpinning {Name} from the Dock settings section failed", row.DisplayName);
        }
    }

    /// <summary>
    /// Moves a pin one place along the dock. The order is committed through the same presenter the
    /// desktop dock's own drag commits through, so both surfaces write one order.
    /// </summary>
    public async Task MoveAsync(DockPinRow row, int offset)
    {
        ArgumentNullException.ThrowIfNull(row);

        var from = IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Rows.Count)
        {
            return;
        }

        try
        {
            await _presenter.MoveAsync(row.Id, to);
        }
        catch (Exception ex)
        {
            _pinnedLog.LogError(ex, "Reordering the pinned applications failed");
        }
    }

    /// <summary>Starts a pinned application. The one thing the settings surface does that is not editing.</summary>
    public async Task LaunchAsync(DockPinRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            await _presenter.LaunchAsync(row.Id);
        }
        catch (Exception ex)
        {
            _pinnedLog.LogError(ex, "Launching {Name} from the Dock settings section failed", row.DisplayName);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The rows carry the "this pin's file has gone" line, which is written in the user's language when a
    /// pin is projected. Re-projecting is therefore how a language change reaches them, through the same
    /// path an add, a remove and a reorder take — there is no second rendering rule for text.
    /// </remarks>
    public override void OnLanguageChanged() => _ = RestoreAsync();

    /// <summary>
    /// Lets go of everything this section was holding: the rows' forwarding subscriptions, the presenter's
    /// subscription to the pin service, and the base class's language registration.
    /// </summary>
    /// <remarks>
    /// This view model is transient — a new one is built for every visit to the settings page — while the pin
    /// service and the localization service both outlive it. Without this the presenter would keep projecting
    /// on every pin change for a page that is gone, once per visit.
    /// </remarks>
    public override void DetachFromPage()
    {
        // Rows are dropped before the presenter, so a final projection arriving later has nothing left to
        // forward to and no orphan row is left holding an item subscription.
        foreach (var row in Rows)
        {
            row.Detach();
        }

        Rows.Clear();

        _presenter.Projected -= OnProjected;
        _presenter.Detach();
        base.DetachFromPage();
    }

    private int IndexOf(DockPinRow row)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], row))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool CanMoveLeft(int index) => index > 0;

    private static bool CanMoveRight(int index, int count) => index >= 0 && index + 1 < count;

    private void OnProjected(object? sender, EventArgs args)
    {
        // Rows are rebuilt on every projection, so the outgoing ones are taken off their item first. A row
        // forwards its item's notifications, and a row that kept that subscription after being dropped would
        // hold the view model alive through `_owner` for as long as the item lives — one orphan per pin per
        // projection, growing for the life of the process.
        foreach (var previous in Rows)
        {
            previous.Detach();
        }

        Rows.Clear();

        var items = _presenter.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var row = new DockPinRow(this, items[i], i, items.Count);
            Rows.Add(row);
            _ = LoadIconAsync(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFull));
    }

    private async Task LoadIconAsync(DockPinRow row)
    {
        try
        {
            await row.Item.LoadIconAsync(_icons);
        }
        catch (Exception ex)
        {
            // An icon the shell would not give up is not a reason to lose the row; the fallback glyph
            // stays and the pin is unaffected.
            _pinnedLog.LogWarning(ex, "The icon for the pinned app {Name} could not be loaded", row.DisplayName);
        }
    }
}

/// <summary>
/// One pinned app as the Dock section draws it: the picture, the name, where it points, and the actions
/// that can be taken on it in dock order.
/// </summary>
/// <remarks>
/// <para>
/// Built on the view model's behalf and rebuilt on every projection, because a move changes the index of
/// every row after it: the commands describe a position, so they are re-made rather than re-evaluated.
/// </para>
/// <para>
/// The row forwards its item's notifications rather than re-implementing them. A pin is drawn with a
/// fallback glyph first and its real icon arrives from the shell a moment later, and that arrival is the
/// only thing about a row that changes after it has been built — so a row that did not forward it would
/// show a placeholder forever.
/// </para>
/// <para>
/// That subscription is the row's whole lifetime. <see cref="Detach"/> therefore has to be called by
/// whoever drops the row, which is the view model's projection; the class is disposable so the owner has one
/// obvious place to put it rather than having to remember which event it was.
/// </para>
/// </remarks>
public sealed class DockPinRow : System.ComponentModel.INotifyPropertyChanged, IDisposable
{
    private readonly DockPinnedAppsViewModel _owner;
    private System.ComponentModel.PropertyChangedEventHandler? _propertyChanged;
    private bool _detached;

    internal DockPinRow(DockPinnedAppsViewModel owner, PinnedAppViewItem item, int index, int count)
    {
        _owner = owner;
        Item = item;
        MoveLeft = new DockPinCommand(index > 0, () => _owner.MoveAsync(this, -1));
        MoveRight = new DockPinCommand(index + 1 < count, () => _owner.MoveAsync(this, 1));
        Remove = new DockPinCommand(true, () => _owner.RemoveAsync(this));
        Launch = new DockPinCommand(item.IsAvailable, () => _owner.LaunchAsync(this));

        Item.PropertyChanged += OnItemChanged;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged
    {
        add => _propertyChanged += value;
        remove => _propertyChanged -= value;
    }

    internal PinnedAppViewItem Item { get; }

    public string Id => Item.Id;

    public string DisplayName => Item.DisplayName;

    /// <summary>Where the pin points. Shown as the row's secondary line, so a broken pin is diagnosable.</summary>
    public string LaunchTarget => Item.LaunchTarget;

    public object IconContent => Item.IconContent;

    public bool IsUnavailable => Item.IsUnavailable;

    /// <summary>Why the pin cannot be used, or null when there is nothing to warn about.</summary>
    public string? Warning => Item.Warning;

    public DockPinCommand MoveLeft { get; }

    public DockPinCommand MoveRight { get; }

    public DockPinCommand Remove { get; }

    public DockPinCommand Launch { get; }

    /// <summary>
    /// Stops forwarding the item's notifications. Idempotent, so a row that was already dropped is not
    /// unsubscribed twice and no caller has to know whether another one got there first.
    /// </summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        Item.PropertyChanged -= OnItemChanged;
        _propertyChanged = null;
    }

    /// <inheritdoc cref="Detach" />
    public void Dispose() => Detach();

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_detached)
        {
            return;
        }

        _propertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(args.PropertyName));
    }
}

/// <summary>
/// A button's action, with the one piece of state a button needs: whether it can be used at all.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a toolkit because it is the only command this section needs, and
/// because the alternative was a per-row lambda with no way to express "this is the first pin, so it
/// cannot move left". The action is allowed to be asynchronous — unpinning and reordering both are — so
/// the task is started here and its failure is the owner's to log, never an unobserved exception on the
/// dispatcher.
/// </remarks>
public sealed class DockPinCommand : System.Windows.Input.ICommand
{
    private readonly Func<Task> _execute;
    private readonly bool _canExecute;

    internal DockPinCommand(bool canExecute, Func<Task> execute)
    {
        _canExecute = canExecute;
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute;

    public void Execute(object? parameter)
    {
        if (_canExecute)
        {
            _ = _execute();
        }
    }

    /// <summary>Tells the button to re-read <see cref="CanExecute"/>.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
