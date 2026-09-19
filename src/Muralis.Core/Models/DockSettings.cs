using Muralis.Core.DockShell;

namespace Muralis.Core.Models;

/// <summary>The dock-wide surface behind its icons. Transparent is the product default.</summary>
public enum DockBackgroundStyle
{
    Transparent,
    Glass,
}

/// <summary>
/// The dock's own settings: whether it is on the desktop at all, and what the user has pinned to it.
/// This is deliberately its own section rather than part of the desktop experience or the Phase 3
/// canvas document — the dock is a product surface that is useful on a fully native desktop.
/// </summary>
public sealed class DockSettings
{
    /// <summary>
    /// Whether the dock is shown on the desktop. It is the product's own surface, so it is on unless
    /// the user turns it off; pinning nothing still shows the zone and its Add button.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Visual policy only; it never changes Dock visibility or lifecycle.</summary>
    public DockBackgroundStyle BackgroundStyle { get; set; } = DockBackgroundStyle.Transparent;

    public List<PinnedAppSettings> PinnedApps { get; set; } = [];
}

/// <summary>
/// One pinned application as it lives on disk. Everything here is a value the user chose, so the
/// record survives being opened and closed without the file behind it having to exist: a pin whose
/// target was deleted stays in the file and is reported as unavailable instead of vanishing.
/// </summary>
public sealed class PinnedAppSettings
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The program or shortcut the shell is asked to open.</summary>
    public string LaunchTarget { get; set; } = string.Empty;

    /// <summary>The path whose icon the shell is asked for. The launch target unless overridden.</summary>
    public string IconIdentity { get; set; } = string.Empty;

    public PinnedAppKind Kind { get; set; } = PinnedAppKind.Application;

    /// <summary>What makes this the same application as another pin. See <see cref="PinnedAppIdentity"/>.</summary>
    public string Identity { get; set; } = string.Empty;

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public static PinnedAppSettings From(PinnedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return new PinnedAppSettings
        {
            Id = app.Id,
            DisplayName = app.DisplayName,
            LaunchTarget = app.LaunchTarget,
            IconIdentity = app.IconIdentity,
            Kind = app.Kind,
            Identity = app.Identity,
            Arguments = app.Arguments,
            WorkingDirectory = app.WorkingDirectory,
        };
    }

    /// <summary>
    /// Reads one entry back. An entry that cannot describe an application is refused rather than
    /// repaired into something the user did not pin; one that is merely incomplete is completed from
    /// what it does say, so a hand-edited file keeps working.
    /// </summary>
    public bool TryToPinnedApp(out PinnedApp? app)
    {
        app = null;

        if (string.IsNullOrWhiteSpace(Id)
            || string.IsNullOrWhiteSpace(DisplayName)
            || string.IsNullOrWhiteSpace(LaunchTarget)
            || !Enum.IsDefined(Kind))
        {
            return false;
        }

        var identity = string.IsNullOrWhiteSpace(Identity)
            ? PinnedAppIdentity.Of(LaunchTarget, null)
            : Identity;
        var icon = string.IsNullOrWhiteSpace(IconIdentity) ? LaunchTarget : IconIdentity;

        app = new PinnedApp(Id, DisplayName, LaunchTarget, icon, Kind, identity, Arguments, WorkingDirectory);
        return true;
    }
}
