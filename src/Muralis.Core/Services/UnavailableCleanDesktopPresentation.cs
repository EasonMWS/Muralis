using Muralis.Core.Abstractions;

namespace Muralis.Core.Services;

/// <summary>
/// Phase 4A safety implementation. The contract is registered now, but Clean Desktop cannot be selected
/// until a real desktop Shelf host can be mounted and torn down together with native icon visibility.
/// </summary>
public sealed class UnavailableCleanDesktopPresentation : ICleanDesktopPresentation
{
    public bool IsAvailable => false;

    public bool IsNativeDesktopHidden => false;

    public Task<CleanDesktopPresentationResult> ActivateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CleanDesktopPresentationResult(
            false,
            "Clean Desktop is not available until the desktop Shelf host is complete."));

    public Task<CleanDesktopPresentationResult> DeactivateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CleanDesktopPresentationResult.Inactive);

    public Task<CleanDesktopPresentationResult> SynchronizeStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CleanDesktopPresentationResult.Inactive);

    public Task<CleanDesktopPresentationResult> RecoverIfNeededAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CleanDesktopPresentationResult.Inactive);
}
