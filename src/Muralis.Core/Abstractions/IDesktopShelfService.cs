using Muralis.Core.DockShell;

namespace Muralis.Core.Abstractions;

/// <summary>Read-only, live projection of the user and public Windows desktop folders.</summary>
public interface IDesktopShelfService : IDisposable
{
    DesktopShelfSnapshot Snapshot { get; }

    event EventHandler<DesktopShelfSnapshot>? Changed;

    Task<DesktopShelfSnapshot> StartAsync(CancellationToken cancellationToken = default);

    Task<DesktopShelfSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    Task OpenAsync(DesktopShelfItem item, CancellationToken cancellationToken = default);
}

public sealed record ShellIconData(int Width, int Height, byte[] PremultipliedBgra);

public interface IShellIconProvider : IDisposable
{
    int CacheCount { get; }

    Task<ShellIconData?> GetAsync(string path, int pixelSize = 48, CancellationToken cancellationToken = default);
}
