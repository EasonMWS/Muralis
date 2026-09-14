using Muralis.Core.Abstractions;

namespace Muralis.Core.Tests.TestSupport;

/// <summary>In-memory provider credentials for tests.</summary>
internal sealed class FakeProviderConfiguration : IProviderConfiguration
{
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeProviderConfiguration WithKey(string providerId, string apiKey)
    {
        _keys[providerId] = apiKey;
        return this;
    }

    public string? GetApiKey(string providerId) => _keys.GetValueOrDefault(providerId);
}
