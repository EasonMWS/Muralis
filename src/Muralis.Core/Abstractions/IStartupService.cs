namespace Muralis.Core.Abstractions;

/// <summary>Registers the application for launch at user sign-in.</summary>
public interface IStartupService
{
    bool IsEnabled();

    /// <summary>Adds or removes the startup registration for the current user.</summary>
    void SetEnabled(bool enabled);
}
