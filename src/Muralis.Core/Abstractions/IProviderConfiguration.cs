namespace Muralis.Core.Abstractions;

/// <summary>
/// Supplies provider credentials from outside the repository: an optional configuration
/// file in the user's data folder, overridden by environment variables. Secrets are never
/// compiled into the application.
/// </summary>
public interface IProviderConfiguration
{
    /// <summary>
    /// The API key configured for <paramref name="providerId"/>, or <c>null</c> when none is set.
    /// The environment variable <c>MURALIS_PROVIDER_{ID}_API_KEY</c> wins over the configuration file.
    /// </summary>
    string? GetApiKey(string providerId);
}
