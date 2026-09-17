using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Abstractions;
using Muralis.Desktop.Launch;
using Xunit;

namespace Muralis.Desktop.Tests.Launch;

/// <summary>
/// The launcher's own rules, tested without starting anything: a target that has gone and a request
/// with nothing in it are the two answers the dock has to be able to act on, and neither of them
/// needs a process to be started to be checked.
/// </summary>
public sealed class ShellApplicationLauncherTests
{
    private readonly ShellApplicationLauncher _launcher = new(NullLogger<ShellApplicationLauncher>.Instance);

    [Fact]
    public async Task ARequestWithNothingInIt_IsRefusedRatherThanStarted()
    {
        var result = await _launcher.LaunchAsync(new ApplicationLaunchRequest(string.Empty));

        Assert.Equal(ApplicationLaunchOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ATargetThatIsGone_ComesBackAsMissing()
    {
        var gone = Path.Combine(Path.GetTempPath(), "muralis-launch-" + Guid.NewGuid().ToString("N"), "gone.exe");

        var result = await _launcher.LaunchAsync(new ApplicationLaunchRequest(gone));

        Assert.Equal(ApplicationLaunchOutcome.Missing, result.Outcome);
        Assert.Equal(gone, result.Error);
    }

    [Fact]
    public async Task ALaunchThatIsAlreadyCancelled_IsCancelledRatherThanStarted()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _launcher.LaunchAsync(new ApplicationLaunchRequest("anything.exe"), cancellation.Token));
    }
}
