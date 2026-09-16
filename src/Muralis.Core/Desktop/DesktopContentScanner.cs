using Microsoft.Extensions.Logging;

namespace Muralis.Core.Desktop;

/// <summary>
/// Reads the user's own desktop: which entries are there, and which of them could be shown on the
/// canvas. It only ever looks — every entry becomes a target that points at where the file already is,
/// and nothing about the desktop, its files or their attributes is written to.
/// </summary>
/// <remarks>
/// <para>
/// Two folders are read: the user's own desktop and the one shared by everyone, in the order Explorer
/// draws them. A file in the user's own desktop shadows one of the same name in the shared folder,
/// exactly as it does on the native desktop, so the shadowed one is reported as skipped rather than
/// adopted twice.
/// </para>
/// <para>
/// The entries Explorer itself does not draw are left alone: the folder's own settings file, and
/// anything marked hidden or system. Adopting what the user cannot see on their own desktop would put
/// something on the canvas they never put there.
/// </para>
/// <para>
/// A scan holds nothing and watches nothing. It is one listing per folder, run when the user asks for
/// one — not on a timer, and never per frame.
/// </para>
/// </remarks>
public sealed class DesktopContentScanner
{
    /// <summary>A file named as an internet shortcut, whose address is read out of it.</summary>
    private const string InternetShortcutExtension = ".url";

    /// <summary>
    /// The most of an internet shortcut that will be read. The format is a few lines of text; a file
    /// that is larger than this is not one, and reading it whole would be reading an arbitrary file.
    /// </summary>
    private const long MaxInternetShortcutBytes = 64 * 1024;

    private static readonly string[] NeverAdoptedNames = ["desktop.ini", "thumbs.db"];

    private readonly ILogger<DesktopContentScanner> _logger;
    private readonly IReadOnlyList<string> _folders;

    public DesktopContentScanner(ILogger<DesktopContentScanner> logger, IReadOnlyList<string>? folders = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _folders = folders ?? DefaultFolders();
    }

    /// <summary>The folders a scan reads unless another set is handed in.</summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        var folders = new List<string>(2);

        // The order matters: the user's own desktop is read first, so its files are the ones that win
        // the name they share with a shared one.
        foreach (var special in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var path = Environment.GetFolderPath(special);
            if (!string.IsNullOrWhiteSpace(path))
            {
                folders.Add(path);
            }
        }

        return folders;
    }

    /// <summary>
    /// The folders this scanner reads. Handed out so a watcher can watch exactly what a scan would read,
    /// which keeps the two from disagreeing about what the user's desktop is.
    /// </summary>
    public IReadOnlyList<string> Folders => _folders;

    /// <summary>Reads the desktop folders this scanner was built with.</summary>
    public DesktopContentScan Scan() => Scan(_folders);

    /// <summary>
    /// Reads the desktop folders on the pool. A desktop on a drive that has gone to sleep must not
    /// stall the page that asked for the count, so nothing here runs on the caller's thread.
    /// </summary>
    public Task<DesktopContentScan> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Scan, cancellationToken);

    /// <summary>
    /// Reads the given folders and sorts their entries into what could be adopted and what could not.
    /// </summary>
    public DesktopContentScan Scan(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        var adoptable = new List<DesktopContentEntry>();
        var skipped = new List<DesktopContentSkip>();
        var unreadable = new List<string>();
        var sourcesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var namesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            string[] entries;
            try
            {
                if (!Directory.Exists(folder))
                {
                    unreadable.Add(folder);
                    _logger.LogWarning("The desktop folder {Folder} is not there to be read", folder);
                    continue;
                }

                entries = Directory.GetFileSystemEntries(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(folder);
                _logger.LogWarning(ex, "The desktop folder {Folder} could not be read", folder);
                continue;
            }

            // The shell draws a desktop alphabetically; reading it the same way keeps the adopted
            // items in an order the user recognises rather than one that depends on the file system.
            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);

            foreach (var path in entries)
            {
                Read(folder, path, adoptable, skipped, sourcesSeen, namesSeen);
            }
        }

        _logger.LogInformation(
            "The desktop was read: {Adoptable} entries could be shown and {Skipped} could not",
            adoptable.Count,
            skipped.Count);

        return new DesktopContentScan(adoptable, skipped, unreadable);
    }

    private void Read(
        string folder,
        string path,
        List<DesktopContentEntry> adoptable,
        List<DesktopContentSkip> skipped,
        HashSet<string> sourcesSeen,
        HashSet<string> namesSeen)
    {
        var name = Path.GetFileName(path);

        if (NeverAdoptedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // The folder's own settings file, which Explorer never draws either.
            skipped.Add(new DesktopContentSkip(path, name, "Windows keeps it hidden on the desktop"));
            return;
        }

        if (!sourcesSeen.Add(path))
        {
            // The same path cannot be reached twice from different folders; a junction may still try.
            return;
        }

        string? reason;
        DesktopItemTarget? target;
        try
        {
            target = Classify(path, out reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // It went away, or it is not something this process may look at. Either way nothing about
            // it is changed and nothing about it is adopted.
            _logger.LogWarning(ex, "The desktop entry {Path} could not be read", path);
            skipped.Add(new DesktopContentSkip(path, name, "It could not be read"));
            return;
        }

        if (target is null)
        {
            skipped.Add(new DesktopContentSkip(path, name, reason!));
            return;
        }

        // Only an entry that would be shown claims its name, so an entry in the shared folder that the
        // user's own desktop does not draw — because it is hidden, or because it cannot be shown — is
        // still free to be adopted under it.
        if (!namesSeen.Add(name))
        {
            skipped.Add(new DesktopContentSkip(path, name, "A file with the same name is already on your own desktop"));
            return;
        }

        adoptable.Add(new DesktopContentEntry(path, Caption(path, name), target));
    }

    /// <summary>
    /// What an entry is, or null and a sentence saying why it cannot be one. The file system decides:
    /// a directory is a folder, and otherwise the extension says which of the shell's own types it is.
    /// </summary>
    private static DesktopItemTarget? Classify(string path, out string? reason)
    {
        reason = null;

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            reason = "Windows keeps it hidden on the desktop";
            return null;
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            return new FolderTarget { Path = path };
        }

        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".lnk":
                return new ShortcutTarget { Path = path };

            case ".exe":
                return new ApplicationTarget { Path = path };

            case InternetShortcutExtension:
                return ReadInternetShortcut(path, out reason);

            default:
                // Anything else is a file the shell opens with whatever is associated with it, which
                // is exactly what double-clicking it on the native desktop does.
                return new FileTarget { Path = path };
        }
    }

    /// <summary>
    /// An internet shortcut as an item, by reading the address it holds. Only http and https are
    /// adopted: the shell opens any registered protocol, and an item must not become a way to start
    /// something by dressing it up as a link.
    /// </summary>
    private static DesktopItemTarget? ReadInternetShortcut(string path, out string? reason)
    {
        reason = null;

        if (new FileInfo(path).Length > MaxInternetShortcutBytes)
        {
            reason = "It is too large to be an internet shortcut";
            return null;
        }

        string? address = null;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            address = trimmed[4..].Trim();
            break;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            reason = "It holds no address";
            return null;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            reason = "The address it holds is not an http or https address";
            return null;
        }

        return new UrlTarget { Url = address };
    }

    /// <summary>
    /// What the item is called: the file's own name, which is what the shell shows for an entry whose
    /// extension it hides. The name is a caption only — the source path is what a later sync compares.
    /// </summary>
    private static string Caption(string path, string fileName)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? fileName : name;
    }
}
