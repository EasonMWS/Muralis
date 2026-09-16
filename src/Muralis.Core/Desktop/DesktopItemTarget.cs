using System.Text.Json.Serialization;

namespace Muralis.Core.Desktop;

/// <summary>What a desktop item opens.</summary>
public enum DesktopItemKind
{
    /// <summary>A Win32 executable.</summary>
    Application,

    /// <summary>A <c>.lnk</c>, resolved by the shell when it is opened.</summary>
    Shortcut,

    /// <summary>Any other file, opened with whatever the system associates with it.</summary>
    File,

    /// <summary>A directory, opened in the shell.</summary>
    Folder,

    /// <summary>A web address, opened in the default browser.</summary>
    Url,
}

/// <summary>
/// What a desktop item points at: one type per kind, so each kind carries exactly the facts it
/// needs and a plain path string is never asked to mean several things. The document writes a
/// <c>kind</c> discriminator, which keeps the file readable by hand and leaves room for kinds the
/// prototype does not model yet — a packaged app, for instance, would arrive as a new derived type
/// with its own fields and old documents would keep loading untouched.
/// </summary>
/// <remarks>
/// The item is only ever a reference: Muralis never copies, moves or rewrites what a target points
/// at, and a target that stops existing leaves the item in place with a missing mark.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ApplicationTarget), "application")]
[JsonDerivedType(typeof(ShortcutTarget), "shortcut")]
[JsonDerivedType(typeof(FileTarget), "file")]
[JsonDerivedType(typeof(FolderTarget), "folder")]
[JsonDerivedType(typeof(UrlTarget), "url")]
public abstract class DesktopItemTarget
{
    /// <summary>The kind this target describes; kept in step with the concrete type by construction.</summary>
    [JsonIgnore]
    public abstract DesktopItemKind Kind { get; }

    /// <summary>
    /// The one string every kind can show and log: the file system path, or the address for a
    /// <see cref="UrlTarget"/>.
    /// </summary>
    [JsonIgnore]
    public abstract string Location { get; }
}

/// <summary>An executable, launched through its own file.</summary>
public sealed class ApplicationTarget : DesktopItemTarget
{
    public string Path { get; set; } = string.Empty;

    public override DesktopItemKind Kind => DesktopItemKind.Application;

    public override string Location => Path;
}

/// <summary>A shell shortcut; the shell resolves what it points at, so Muralis does not have to.</summary>
public sealed class ShortcutTarget : DesktopItemTarget
{
    public string Path { get; set; } = string.Empty;

    public override DesktopItemKind Kind => DesktopItemKind.Shortcut;

    public override string Location => Path;
}

/// <summary>A file opened with its default association.</summary>
public sealed class FileTarget : DesktopItemTarget
{
    public string Path { get; set; } = string.Empty;

    public override DesktopItemKind Kind => DesktopItemKind.File;

    public override string Location => Path;
}

/// <summary>A directory, opened in the shell.</summary>
public sealed class FolderTarget : DesktopItemTarget
{
    public string Path { get; set; } = string.Empty;

    public override DesktopItemKind Kind => DesktopItemKind.Folder;

    public override string Location => Path;
}

/// <summary>
/// A web address. Only <c>http</c> and <c>https</c> are accepted: the shell would happily open
/// anything with a registered protocol handler, and a desktop item must not become a way to run a
/// program by dressing it up as a link.
/// </summary>
public sealed class UrlTarget : DesktopItemTarget
{
    public string Url { get; set; } = string.Empty;

    public override DesktopItemKind Kind => DesktopItemKind.Url;

    public override string Location => Url;
}
