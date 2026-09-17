using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.DockShell;
using Muralis.Desktop.Interop;

namespace Muralis.Desktop.Launch;

/// <summary>
/// Answers what a file the user wants to pin actually is, using what the shell and the file itself
/// already say rather than guessing from the name: a shortcut is resolved through the shell's own
/// shortcut object, and a program is named by its own version information.
/// </summary>
/// <remarks>
/// <para>
/// The resolved target is the point of resolving at all: a shortcut and the program it points at have
/// to be recognised as the same application, and only the shell knows where a shortcut goes.
/// </para>
/// <para>
/// Nothing is written: the shortcut object is loaded read-only, one shortcut per call, and released
/// before the call returns. A shortcut that cannot be read — locked, damaged, or pointing at a
/// packaged application through an app-model identity rather than a path — is described without a
/// resolved target rather than refused, because the shell can still open it.
/// </para>
/// </remarks>
public sealed class ShellApplicationInspector : IApplicationInspector
{
    private readonly ILogger<ShellApplicationInspector> _logger;

    public ShellApplicationInspector(ILogger<ShellApplicationInspector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<ApplicationDescription?> DescribeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Task.FromResult<ApplicationDescription?>(null);
        }

        var kind = PinnedAppTargets.KindOf(path);
        if (kind is null)
        {
            return Task.FromResult<ApplicationDescription?>(null);
        }

        // The shell's shortcut object is reached through COM and the version information is read from
        // the file, so neither belongs on the UI thread: the dock shows the pin once this answers.
        return Task.Run(
            () => kind == PinnedAppKind.Application ? DescribeApplication(path) : DescribeShortcut(path),
            CancellationToken.None);
    }

    private ApplicationDescription DescribeApplication(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            name = FirstUsableName(version.FileDescription, version.ProductName, name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // A program with no readable version information is still a program; its file name names it.
            _logger.LogDebug(ex, "The version information for {Path} could not be read", path);
        }

        _logger.LogDebug("The program {Path} was described as {Name}", path, name);
        return new ApplicationDescription(PinnedAppKind.Application, name, path, null);
    }

    private ApplicationDescription? DescribeShortcut(string path)
    {
        // A shortcut is named the way Explorer names it: the name the user gave the file.
        var name = Path.GetFileNameWithoutExtension(path);
        var resolved = ResolveShortcut(path);

        _logger.LogDebug(
            "The shortcut {Path} was described as {Name}, pointing at {Target}",
            path,
            name,
            string.IsNullOrWhiteSpace(resolved) ? "nothing it can say" : resolved);

        return new ApplicationDescription(PinnedAppKind.Shortcut, name, path, resolved);
    }

    /// <summary>
    /// The path a shortcut stores, or null when the shell will not say — which is the case for a
    /// shortcut that opens a packaged application rather than a file.
    /// </summary>
    private string? ResolveShortcut(string path)
    {
        object? link = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ShellLinkInterfaces.ShellLinkClassId);
            if (type is null)
            {
                return null;
            }

            link = Activator.CreateInstance(type);
            if (link is null)
            {
                return null;
            }

            // Loading read-only is what stops the shell from touching the user's shortcut: it is read
            // for its target and nothing about it is changed or repaired.
            ((ShellLinkInterfaces.IPersistFile)link).Load(path, ShellLinkInterfaces.StorageRead);

            var buffer = new StringBuilder(ShellLinkInterfaces.MaximumPathLength);
            ((ShellLinkInterfaces.IShellLinkW)link).GetPath(buffer, buffer.Capacity, nint.Zero, 0);
            var target = buffer.ToString().Trim();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "The shortcut {Path} could not be resolved; it will be pinned as itself", path);
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    /// <summary>
    /// The first name that can be shown. Version information is occasionally a resource reference
    /// rather than text — a leading <c>@</c> is the tell — and that is not a name to put in the dock.
    /// </summary>
    private static string FirstUsableName(string? description, string? product, string fallback)
    {
        foreach (var candidate in new[] { description, product })
        {
            var trimmed = candidate?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('@'))
            {
                return trimmed;
            }
        }

        return fallback;
    }
}
