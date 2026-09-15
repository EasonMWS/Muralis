using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Muralis.Core.Models;

/// <summary>Where a queued download is in its lifecycle.</summary>
public enum DownloadState
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One entry in the download queue. The values the UI shows change while the item is
/// processed, so the item raises change notifications like <see cref="Wallpaper"/> does.
/// The state machine itself is owned by <c>DownloadQueueService</c>.
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    private DownloadState _state = DownloadState.Queued;
    private double _progress;
    private int _attempts;
    private string? _failureReason;
    private string? _localPath;
    private bool _isDuplicate;

    public DownloadItem(Wallpaper wallpaper, string targetDirectory, int maxAttempts = 3)
    {
        Wallpaper = wallpaper ?? throw new ArgumentNullException(nameof(wallpaper));
        TargetDirectory = targetDirectory;
        MaxAttempts = Math.Max(1, maxAttempts);
    }

    public Wallpaper Wallpaper { get; }

    public string WallpaperId => Wallpaper.Id;

    public string Title => Wallpaper.Title;

    /// <summary>Folder the file is written to, resolved when the download was queued.</summary>
    public string TargetDirectory { get; }

    /// <summary>Automatic attempts (including the first) before the item is marked failed.</summary>
    public int MaxAttempts { get; }

    public DownloadState State
    {
        get => _state;
        internal set => SetField(ref _state, value);
    }

    /// <summary>Transfer progress, 0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        internal set => SetField(ref _progress, value);
    }

    /// <summary>How many transfer attempts have been made so far.</summary>
    public int Attempts
    {
        get => _attempts;
        internal set => SetField(ref _attempts, value);
    }

    /// <summary>Message of the last failed attempt, kept for the retry tooltip.</summary>
    public string? FailureReason
    {
        get => _failureReason;
        internal set => SetField(ref _failureReason, value);
    }

    /// <summary>Full path of the downloaded file once the item completed.</summary>
    public string? LocalPath
    {
        get => _localPath;
        internal set => SetField(ref _localPath, value);
    }

    /// <summary>
    /// True when the transfer finished but the identical image was already in the library,
    /// whose existing file was kept instead of the fresh copy.
    /// </summary>
    public bool IsDuplicate
    {
        get => _isDuplicate;
        internal set => SetField(ref _isDuplicate, value);
    }

    internal CancellationTokenSource Cancellation { get; private set; } = new();

    /// <summary>True while the item still has work left (waiting or transferring).</summary>
    public bool IsActive => State is DownloadState.Queued or DownloadState.Downloading;

    internal void RequestCancel() => Cancellation.Cancel();

    /// <summary>Puts the item back in line, with a fresh cancellation source for the new round.</summary>
    internal void PrepareForRetry()
    {
        var previous = Cancellation;
        Cancellation = new CancellationTokenSource();
        previous.Dispose();

        Attempts = 0;
        Progress = 0;
        FailureReason = null;
        IsDuplicate = false;
        State = DownloadState.Queued;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);

        if (propertyName == nameof(State))
        {
            OnPropertyChanged(nameof(IsActive));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
