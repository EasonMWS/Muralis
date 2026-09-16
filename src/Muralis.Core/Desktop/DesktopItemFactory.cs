namespace Muralis.Core.Desktop;

/// <summary>
/// Turns what the user picked into a desktop item: the kind comes from what the path is, the name
/// from the file name, and the item only ever references what was picked. Nothing is copied, moved
/// or scanned — the import entry hands in one path at a time, chosen by the user.
/// </summary>
public static class DesktopItemFactory
{
    /// <summary>
    /// Builds an item for a file system path. Directories, shortcuts and executables each get their
    /// own target kind; anything else is a plain file that the shell opens with its usual program.
    /// </summary>
    public static DesktopItem CreateFromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileNameWithoutExtension(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        DesktopItemTarget target = Directory.Exists(fullPath)
            ? new FolderTarget { Path = fullPath }
            : Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".lnk" => new ShortcutTarget { Path = fullPath },
                ".exe" => new ApplicationTarget { Path = fullPath },
                _ => new FileTarget { Path = fullPath },
            };

        return Build(target, string.IsNullOrWhiteSpace(name) ? fullPath : name);
    }

    /// <summary>
    /// Builds an item for a target that has already been classified, which is how the desktop scan
    /// hands one in: it reads a <c>.url</c> file's address and a folder's nature itself, and asking the
    /// path again would be a second, different answer. The source is recorded so a later sync knows the
    /// item came from a desktop entry rather than from the import box.
    /// </summary>
    public static DesktopItem CreateFromTarget(DesktopItemTarget target, string name, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var item = Build(target, name);
        item.SourcePath = sourcePath;
        return item;
    }

    /// <summary>
    /// Builds an item that opens an address in the default browser. Addresses without a scheme get
    /// <c>https</c>; anything that is not http or https is refused, because an item may not become a
    /// way to start a program the user did not pick.
    /// </summary>
    public static DesktopItem CreateFromUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            // Without a scheme the shell would treat the text as a file name; "https" is what the
            // user meant by typing a bare address.
            trimmed = "https://" + trimmed;
            Uri.TryCreate(trimmed, UriKind.Absolute, out parsed);
        }

        if (parsed is null || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{url}' is not an http or https address.", nameof(url));
        }

        var name = parsed.Host is { Length: > 0 } host ? host : trimmed;

        return Build(new UrlTarget { Url = trimmed }, name);
    }

    /// <summary>The one place an item is put together, so every way in produces the same shape.</summary>
    private static DesktopItem Build(DesktopItemTarget target, string name) => new()
    {
        Id = NewId(target.Kind),
        Name = name,
        Target = target,
        IconKey = IconKeyFor(target.Kind),
    };

    /// <summary>The glyph used while a real icon is loading, or when the shell has none to give.</summary>
    private static string IconKeyFor(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.Folder => "folder",
        DesktopItemKind.Url => "url",
        _ => string.Empty,
    };

    /// <summary>
    /// A short, kind-prefixed id. Ids are the only identity items have, so they are generated rather
    /// than derived from the target: adding the same program twice stays two items.
    /// </summary>
    private static string NewId(DesktopItemKind kind)
    {
        var prefix = kind switch
        {
            DesktopItemKind.Application => "app",
            DesktopItemKind.Shortcut => "lnk",
            DesktopItemKind.Folder => "dir",
            DesktopItemKind.Url => "url",
            _ => "file",
        };

        return $"{prefix}_{Guid.NewGuid().ToString("N")[..8]}";
    }
}
