namespace Muralis.Core.Abstractions;

/// <summary>
/// UI boundary for the real Muralis desktop dock: the strip that carries the pinned applications, the
/// Shelf and the utilities.
/// </summary>
/// <remarks>
/// The dock is a product surface rather than a mode. It is shown on a fully native desktop, and it is
/// also what Clean Desktop puts in place of Explorer's icons — which is why it must be ready and
/// visible before those icons hide. Nothing here decides whether it should be up; that is
/// <see cref="IDockExperienceService"/>'s.
/// </remarks>
public interface IDesktopDockHost
{
    bool IsAvailable { get; }

    Task PrepareAsync(CancellationToken cancellationToken = default);

    Task ShowAsync(CancellationToken cancellationToken = default);

    Task HideAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the window away for good, because the application is ending. Hiding is not enough for
    /// that: the dock is a window of its own, and a WinUI application only ends once its last window
    /// has closed — a hidden dock would keep the process alive with nothing left on screen to explain
    /// it.
    /// </summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
