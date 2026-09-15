namespace Muralis.Core.Models;

/// <summary>
/// What kind of content a desktop backdrop shows. Only kinds that truly belong to the desktop
/// background may be added here; app content (music, scenes, widgets) is not a backdrop kind.
/// </summary>
public enum BackdropKind
{
    /// <summary>Nothing — the desktop shows whatever is underneath.</summary>
    None,

    /// <summary>A looping video file.</summary>
    Video,
}

/// <summary>
/// Playback policy for a video backdrop. Kept as data (declared by scenes or profiles) so the
/// backdrop service stays parameter-free.
/// </summary>
public sealed record VideoPlaybackOptions(
    bool Loop = true,
    bool Muted = true,
    bool PauseOnFullscreen = true,
    bool PauseOnBattery = false)
{
    public static VideoPlaybackOptions Default { get; } = new();
}

/// <summary>
/// What one display should be showing. <see cref="AssetRef"/> is opaque here: the backdrop
/// service receives it already resolved and never reads profiles or scenes itself.
/// </summary>
public sealed record BackdropSpec(BackdropKind Kind, string? AssetRef = null, VideoPlaybackOptions? Video = null)
{
    /// <summary>No backdrop — the static desktop background stays visible.</summary>
    public static BackdropSpec None { get; } = new(BackdropKind.None);

    public static BackdropSpec ForVideo(string assetRef, VideoPlaybackOptions? options = null) =>
        new(BackdropKind.Video, assetRef, options ?? VideoPlaybackOptions.Default);

    /// <summary>
    /// Checks the cross-field facts a consumer may rely on. Returns an empty list when the spec
    /// is coherent.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Kind == BackdropKind.Video && string.IsNullOrWhiteSpace(AssetRef))
        {
            problems.Add("A video backdrop needs an asset reference.");
        }

        if (Kind == BackdropKind.None && (AssetRef is not null || Video is not null))
        {
            problems.Add("A backdrop with no kind cannot carry an asset or playback options.");
        }

        return problems;
    }
}
