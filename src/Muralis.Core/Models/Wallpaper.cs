using System.ComponentModel;
using System.Runtime.CompilerServices;
using Muralis.Core.Helpers;

namespace Muralis.Core.Models;

/// <summary>
/// Represents a single wallpaper, whether it lives on disk, online, or both.
/// Implements change notification for the properties the UI updates in place
/// (downloads, favorites, cached thumbnails).
/// </summary>
public sealed class Wallpaper : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string? _localPath;
    private string? _cachedThumbnailPath;
    private int _width;
    private int _height;
    private long _fileSize;
    private bool _isFavorite;
    private DateTimeOffset? _lastUsedAt;

    /// <summary>Stable identity, e.g. <c>local:{sha256-of-path}</c> or <c>bing:2026-09-14</c>.</summary>
    public string Id { get; init; } = string.Empty;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    /// <summary>Absolute path of the image file on this machine, if available.</summary>
    public string? LocalPath
    {
        get => _localPath;
        set
        {
            if (SetField(ref _localPath, value))
            {
                OnPropertyChanged(nameof(HasLocalFile));
                OnPropertyChanged(nameof(DisplayImagePath));
            }
        }
    }

    /// <summary>
    /// Thumbnail file downloaded into the app cache. Not persisted; only used to render
    /// online wallpapers before they are downloaded in full.
    /// </summary>
    public string? CachedThumbnailPath
    {
        get => _cachedThumbnailPath;
        set
        {
            if (SetField(ref _cachedThumbnailPath, value))
            {
                OnPropertyChanged(nameof(DisplayImagePath));
            }
        }
    }

    public string? RemoteUrl { get; set; }

    public string? ThumbnailUrl { get; set; }

    public int Width
    {
        get => _width;
        set
        {
            if (SetField(ref _width, value))
            {
                OnPropertyChanged(nameof(ResolutionText));
                OnPropertyChanged(nameof(AspectRatio));
                OnPropertyChanged(nameof(AspectRatioText));
                OnPropertyChanged(nameof(MetaText));
            }
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            if (SetField(ref _height, value))
            {
                OnPropertyChanged(nameof(ResolutionText));
                OnPropertyChanged(nameof(AspectRatio));
                OnPropertyChanged(nameof(AspectRatioText));
                OnPropertyChanged(nameof(MetaText));
            }
        }
    }

    public long FileSize
    {
        get => _fileSize;
        set
        {
            if (SetField(ref _fileSize, value))
            {
                OnPropertyChanged(nameof(FileSizeText));
            }
        }
    }

    public IReadOnlyList<string> Tags { get; set; } = [];

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetField(ref _isFavorite, value);
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastUsedAt
    {
        get => _lastUsedAt;
        set => SetField(ref _lastUsedAt, value);
    }

    public WallpaperSource Source { get; set; } = WallpaperSource.Local;

    public double AspectRatio => Height > 0 ? (double)Width / Height : 0d;

    public bool HasLocalFile => !string.IsNullOrEmpty(LocalPath);

    /// <summary>What the card should render: the local file when present, otherwise the cached thumbnail.</summary>
    public string? DisplayImagePath => LocalPath ?? CachedThumbnailPath;

    public string ResolutionText => DisplayFormat.Resolution(Width, Height);

    public string AspectRatioText => DisplayFormat.AspectRatio(Width, Height);

    public string FileSizeText => FileSize > 0 ? DisplayFormat.FileSize(FileSize) : string.Empty;

    /// <summary>Secondary line on cards: resolution when known, otherwise the source tag.</summary>
    public string MetaText => Width > 0 && Height > 0
        ? ResolutionText
        : Tags.FirstOrDefault() ?? "Online";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
