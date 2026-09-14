using Muralis.Core.Models;

namespace Muralis.Core.Abstractions;

/// <summary>
/// The user's own wallpaper collection. Imported files are referenced in place:
/// the library never copies or modifies the original images.
/// </summary>
public interface ILocalLibrary
{
    IReadOnlyList<Wallpaper> Items { get; }

    event EventHandler? Changed;

    /// <summary>
    /// Adds the given image files to the library, skipping duplicates and unsupported files.
    /// </summary>
    Task<ImportResult> ImportAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>Removes the app-level record. The image file on disk is left untouched.</summary>
    bool Remove(string wallpaperId);

    Wallpaper? Find(string wallpaperId);

    /// <summary>Records that the wallpaper was applied to the desktop.</summary>
    void MarkUsed(string wallpaperId, DateTimeOffset usedAt);
}

public sealed record ImportResult(int Added, int Duplicates, int Failed);
