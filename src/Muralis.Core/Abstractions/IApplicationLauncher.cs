namespace Muralis.Core.Abstractions;

/// <summary>How an attempt to start an application ended.</summary>
public enum ApplicationLaunchOutcome
{
    /// <summary>The shell took the request.</summary>
    Launched,

    /// <summary>The target is gone; the pin stays and is reported as unavailable.</summary>
    Missing,

    /// <summary>The shell refused, or could not start what it was pointed at.</summary>
    Failed,
}

/// <summary>
/// One request to start something. Arguments and the working directory are the user's own settings
/// for this pin, so they are optional and empty means "let the application decide" rather than
/// "pass an empty value".
/// </summary>
public sealed record ApplicationLaunchRequest(
    string Target,
    string? Arguments = null,
    string? WorkingDirectory = null);

public sealed record ApplicationLaunchResult(ApplicationLaunchOutcome Outcome, string? Error)
{
    public static ApplicationLaunchResult Launched { get; } = new(ApplicationLaunchOutcome.Launched, null);

    public static ApplicationLaunchResult Missing(string target) =>
        new(ApplicationLaunchOutcome.Missing, target);

    public static ApplicationLaunchResult Failed(string error) =>
        new(ApplicationLaunchOutcome.Failed, error);
}

/// <summary>
/// Starts an application the way the shell would, so per-user associations, shortcut settings and
/// elevation prompts all behave exactly as they do from Explorer. Implemented per platform for the
/// same reason the desktop item launcher is: callers ask for a launch and are told how it ended, and
/// no UI code builds a command line or starts a process itself.
/// </summary>
public interface IApplicationLauncher
{
    /// <summary>
    /// Starts <paramref name="request"/>. A target that is gone comes back as
    /// <see cref="ApplicationLaunchOutcome.Missing"/> and a refusal as
    /// <see cref="ApplicationLaunchOutcome.Failed"/>; neither is thrown, because both are things to
    /// show the user rather than crashes.
    /// </summary>
    Task<ApplicationLaunchResult> LaunchAsync(
        ApplicationLaunchRequest request,
        CancellationToken cancellationToken = default);
}
