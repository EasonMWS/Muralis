namespace Muralis.Core.Models;

/// <summary>Why a provider can or cannot be used right now.</summary>
public enum ProviderAvailability
{
    /// <summary>Enabled and ready (a required API key, if any, is present).</summary>
    Available,

    /// <summary>Switched off by the user in settings.</summary>
    Disabled,

    /// <summary>Requires an API key and none is configured yet.</summary>
    NeedsApiKey,
}
