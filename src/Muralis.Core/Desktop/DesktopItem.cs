using System.Text.Json.Serialization;
using Muralis.Core.Canvas;

namespace Muralis.Core.Desktop;

/// <summary>
/// One item on the desktop: what it is called, where it sits, and — through
/// <see cref="Target"/> — what it opens. Data only: the saved position is <see cref="Anchor"/>
/// plus the DIP offsets, and pixels are recomputed on every layout pass. <see cref="Z"/> orders
/// the items the canvas shows (higher draws on top).
/// </summary>
/// <remarks>
/// <para>
/// The item never holds the file it points at, only a reference to it; a target that stops
/// existing is reported by <see cref="IsMissing"/> and the item itself stays in the layout until
/// the user takes it away.
/// </para>
/// <para>
/// An item does not say where it lives. The layout's dock names the items it shows and the canvas
/// shows the rest, so moving an item between the two is one entry changing place rather than a
/// second field that has to be kept in step with it.
/// </para>
/// </remarks>
public sealed class DesktopItem
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>The file, folder or address this item opens.</summary>
    public DesktopItemTarget Target { get; set; } = null!;

    /// <summary>
    /// Glyph key for the built-in icons, used while a real icon is being resolved and standing in
    /// for it when the shell has none. Empty falls back to the generic placeholder.
    /// </summary>
    public string IconKey { get; set; } = string.Empty;

    public CanvasAnchor Anchor { get; set; } = CanvasAnchor.Center;

    public double OffsetXDip { get; set; }

    public double OffsetYDip { get; set; }

    /// <summary>Edge length in DIP; items are square.</summary>
    public double SizeDip { get; set; } = 96;

    public int Z { get; set; }

    public bool IsVisible { get; set; } = true;

    /// <summary>Where the target points, as shown in lists and log lines.</summary>
    [JsonIgnore]
    public string Location => Target?.Location ?? string.Empty;

    /// <summary>
    /// Whether the target still exists right now. A missing target only marks the item: it is never
    /// removed, moved or rewritten.
    /// </summary>
    public bool IsMissing() => Target switch
    {
        null => true,
        UrlTarget => false,
        ApplicationTarget app => !File.Exists(app.Path),
        ShortcutTarget link => !File.Exists(link.Path),
        FileTarget file => !File.Exists(file.Path),
        FolderTarget folder => !Directory.Exists(folder.Path),
        _ => true,
    };

    /// <summary>Checks the facts consumers rely on, one message per problem found.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        var what = string.IsNullOrWhiteSpace(Id) ? "an item" : $"The item '{Id}'";

        if (string.IsNullOrWhiteSpace(Id))
        {
            problems.Add("Every item needs a non-empty id.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add($"{what} needs a name.");
        }

        if (SizeDip is < 16 or > 512)
        {
            problems.Add($"{what} has a size outside 16-512 DIP.");
        }

        if (!double.IsFinite(OffsetXDip) || !double.IsFinite(OffsetYDip))
        {
            problems.Add($"{what} has a non-finite offset.");
        }

        if (Target is null)
        {
            problems.Add($"{what} needs a target.");
            return problems;
        }

        if (!Enum.IsDefined(Target.Kind))
        {
            problems.Add($"{what} has an unknown target kind.");
        }

        if (Target is UrlTarget url)
        {
            ValidateUrl(what, url, problems);
        }
        else
        {
            ValidatePath(what, Target.Location, problems);
        }

        return problems;
    }

    /// <summary>Deep copy, safe to hand to another thread while the canvas keeps editing this one.</summary>
    public DesktopItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Target = CloneTarget(Target)!,
        IconKey = IconKey,
        Anchor = Anchor,
        OffsetXDip = OffsetXDip,
        OffsetYDip = OffsetYDip,
        SizeDip = SizeDip,
        Z = Z,
        IsVisible = IsVisible,
    };

    private static DesktopItemTarget? CloneTarget(DesktopItemTarget? target) => target switch
    {
        ApplicationTarget app => new ApplicationTarget { Path = app.Path },
        ShortcutTarget link => new ShortcutTarget { Path = link.Path },
        FileTarget file => new FileTarget { Path = file.Path },
        FolderTarget folder => new FolderTarget { Path = folder.Path },
        UrlTarget url => new UrlTarget { Url = url.Url },
        _ => null,
    };

    private static void ValidatePath(string what, string path, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            problems.Add($"{what} has an empty target path.");
            return;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            problems.Add($"{what} must point at a fully qualified path.");
            return;
        }

        // Paths are only ever handed to the shell, never concatenated, and a path is not a command
        // line: characters the file system cannot represent are refused so a bad hand-edit is caught
        // here instead of at launch time.
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            problems.Add($"{what} has a path with invalid characters.");
        }
    }

    private static void ValidateUrl(string what, UrlTarget url, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(url.Url))
        {
            problems.Add($"{what} has an empty address.");
            return;
        }

        if (!Uri.TryCreate(url.Url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add($"{what} may only open http and https addresses.");
        }
    }
}
