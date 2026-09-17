using Muralis.Core.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The home page's Muralis Mode hero, as a state machine. These are the properties the product depends
/// on: the hero shows the mode the desktop really is in rather than one of its own, entering and leaving
/// are requests to the one desktop service, a request that does not take ends on the desktop that really
/// is in place, and a second press while the first is still landing is not a second request.
/// </summary>
public sealed class MuralisModeHeroTests
{
    private readonly FakeDesktopExperience _desktop = new();

    [Fact]
    public void StartingOnTheNativeDesktop_ShowsReadyToEnter()
    {
        using var hero = new MuralisModeHero(_desktop);

        Assert.Equal(MuralisModeHeroState.Ready, hero.State);
        Assert.Null(hero.Error);
        Assert.Equal(DesktopExperienceMode.Native, hero.Status.Mode);
    }

    [Fact]
    public void StartingWithTheModeAlreadyRunning_ShowsActive()
    {
        // What a launch finds: the mode was left on, the desktop service brought it back, and the hero
        // has nothing to do but show it.
        _desktop.Publish(Running());

        using var hero = new MuralisModeHero(_desktop);

        Assert.Equal(MuralisModeHeroState.Active, hero.State);
    }

    [Fact]
    public void StartingWithAFailureOnRecord_ShowsTheReasonRatherThanClaimingTheMode()
    {
        _desktop.Publish(new DesktopExperienceStatus(
            DesktopExperienceMode.Native,
            true,
            DesktopModeStatus.Native,
            "Muralis Mode could not be started."));

        using var hero = new MuralisModeHero(_desktop);

        Assert.Equal(MuralisModeHeroState.Error, hero.State);
        Assert.Equal("Muralis Mode could not be started.", hero.Error);
        Assert.Equal(DesktopExperienceMode.Native, hero.Status.Mode);
    }

    [Fact]
    public async Task Entering_AsksTheDesktopForTheModeAndEndsRunning()
    {
        using var hero = new MuralisModeHero(_desktop);
        var states = new List<MuralisModeHeroState>();
        hero.StateChanged += (_, state) => states.Add(state);

        await hero.EnterAsync();

        Assert.Equal(new[] { DesktopExperienceMode.Muralis }, _desktop.Requests);
        Assert.Equal(MuralisModeHeroState.Active, hero.State);
        Assert.True(hero.Status.IsMuralis);
        Assert.False(hero.IsBusy);

        // The desktop is rearranged while the request is in flight, and the hero says so for as long as
        // that lasts: that is the window in which its actions are held.
        Assert.Equal(new[] { MuralisModeHeroState.Entering, MuralisModeHeroState.Active }, states);
    }

    [Fact]
    public async Task Leaving_AsksTheDesktopForTheNativeDesktop()
    {
        _desktop.Publish(Running());
        using var hero = new MuralisModeHero(_desktop);

        await hero.ExitAsync();

        Assert.Equal([DesktopExperienceMode.Native], _desktop.Requests);
        Assert.Equal(MuralisModeHeroState.Ready, hero.State);
        Assert.False(hero.Status.IsMuralis);
    }

    [Fact]
    public async Task ARequestThatFailsOpen_EndsOnTheNativeDesktopWithTheReason()
    {
        // The desktop service fails open: it reports the mode that really is in place, which is the native
        // desktop, with what went wrong. The hero must not end this on a mode that is not running.
        _desktop.Respond = _ => Task.FromResult(new DesktopExperienceStatus(
            DesktopExperienceMode.Native,
            true,
            DesktopModeStatus.Native,
            "Muralis Mode could not be started."));

        using var hero = new MuralisModeHero(_desktop);
        await hero.EnterAsync();

        Assert.Equal(MuralisModeHeroState.Error, hero.State);
        Assert.False(hero.Status.IsMuralis);
        Assert.Equal(DesktopExperienceMode.Native, hero.Status.Mode);
        Assert.Equal("Muralis Mode could not be started.", hero.Error);
    }

    [Fact]
    public async Task ARequestThatThrows_EndsOnTheRealModeRatherThanTheOneAskedFor()
    {
        _desktop.Respond = _ => throw new InvalidOperationException("the desktop service fell over");

        using var hero = new MuralisModeHero(_desktop);
        await hero.EnterAsync();

        Assert.Equal(MuralisModeHeroState.Error, hero.State);
        Assert.Equal(DesktopExperienceMode.Native, hero.Status.Mode);
        Assert.Equal("the desktop service fell over", hero.Error);
    }

    [Fact]
    public async Task TryingAgainAfterAFailure_EntersTheModeAndDropsTheReason()
    {
        _desktop.Respond = _ => Task.FromResult(new DesktopExperienceStatus(
            DesktopExperienceMode.Native,
            true,
            DesktopModeStatus.Native,
            "Muralis Mode could not be started."));
        using var hero = new MuralisModeHero(_desktop);
        await hero.EnterAsync();

        _desktop.Respond = null;
        await hero.EnterAsync();

        Assert.Equal(MuralisModeHeroState.Active, hero.State);
        Assert.Null(hero.Error);
    }

    [Fact]
    public async Task ASecondPressWhileTheFirstIsStillLanding_IsNotASecondRequest()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _desktop.Gate = gate;
        using var hero = new MuralisModeHero(_desktop);

        var entering = hero.EnterAsync();

        Assert.True(hero.IsBusy);
        Assert.Equal(MuralisModeHeroState.Entering, hero.State);

        await hero.EnterAsync();
        await hero.ExitAsync();

        Assert.Single(_desktop.Requests);

        gate.SetResult();
        await entering;

        Assert.Equal(MuralisModeHeroState.Active, hero.State);
        Assert.False(hero.IsBusy);
    }

    [Fact]
    public void AModeChangedSomewhereElse_ShowsUpWithoutBeingAsked()
    {
        // The settings page has its own way to change the mode. The hero follows the desktop rather than
        // a copy of it, so it reads the change without a restart and without a request of its own.
        using var hero = new MuralisModeHero(_desktop);

        _desktop.Publish(Running());
        Assert.Equal(MuralisModeHeroState.Active, hero.State);

        _desktop.Publish(Native());
        Assert.Equal(MuralisModeHeroState.Ready, hero.State);
        Assert.Empty(_desktop.Requests);
    }

    [Fact]
    public void Disposing_StopsFollowingTheDesktop()
    {
        var hero = new MuralisModeHero(_desktop);
        hero.Dispose();

        _desktop.Publish(Running());

        Assert.Equal(MuralisModeHeroState.Ready, hero.State);
        hero.Dispose();
    }

    private static DesktopExperienceStatus Running() =>
        new(DesktopExperienceMode.Muralis, true, DesktopModeStatus.Native, null);

    private static DesktopExperienceStatus Native() =>
        new(DesktopExperienceMode.Native, true, DesktopModeStatus.Native, null);

    /// <summary>
    /// Stands in for the desktop experience. It answers with what it is told to answer with, which is what
    /// makes the interesting cases reachable: a request that fails open, a request that falls over, and a
    /// request that is still being worked on while the user presses again.
    /// </summary>
    private sealed class FakeDesktopExperience : IDesktopExperienceService
    {
        public List<DesktopExperienceMode> Requests { get; } = [];

        /// <summary>An answer to give instead of the real one, or a throw.</summary>
        public Func<DesktopExperienceMode, Task<DesktopExperienceStatus>>? Respond { get; set; }

        /// <summary>Holds the request open, so the time before it lands can be looked at.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public DesktopExperienceStatus Status { get; private set; } = Native();

        public event EventHandler<DesktopExperienceStatus>? Changed;

        public Task<DesktopExperienceStatus> RestoreAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public async Task<DesktopExperienceStatus> ApplyAsync(
            DesktopExperienceMode mode,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(mode);

            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }

            // The real service never throws and always reports what is in place; so does this one, except
            // where a test asks for the other behaviour.
            return Publish(Respond is null
                ? mode == DesktopExperienceMode.Muralis ? Running() : Native()
                : await Respond(mode).ConfigureAwait(false));
        }

        public DesktopExperienceStatus Publish(DesktopExperienceStatus status)
        {
            Status = status;
            Changed?.Invoke(this, status);
            return status;
        }
    }
}
