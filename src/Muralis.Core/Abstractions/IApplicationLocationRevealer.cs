namespace Muralis.Core.Abstractions;

/// <summary>
/// Shows a file where it lives, opened in Explorer with the file already selected. Kept apart from
/// <see cref="IApplicationLauncher"/> because revealing is not starting — nothing is executed, and a
/// file that cannot run at all can still be revealed.
/// </summary>
public interface IApplicationLocationRevealer
{
    /// <summary>
    /// Reveals <paramref name="target"/>. Returns false when its folder is gone or Explorer refused;
    /// like every other shell call here, it reports rather than throws.
    /// </summary>
    Task<bool> RevealAsync(string target, CancellationToken cancellationToken = default);
}
