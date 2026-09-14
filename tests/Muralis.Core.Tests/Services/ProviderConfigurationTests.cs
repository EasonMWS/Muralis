using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Services;
using Xunit;

namespace Muralis.Core.Tests.Services;

public sealed class ProviderConfigurationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public ProviderConfigurationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muralis-provider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "providers.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void GetApiKey_WithNoFile_ReturnsNull()
    {
        var configuration = CreateConfiguration();

        Assert.Null(configuration.GetApiKey("nasa"));
    }

    [Fact]
    public void GetApiKey_ReadsTheConfiguredFile()
    {
        File.WriteAllText(_filePath, """
            {
              "providers": {
                "nasa": { "apiKey": "file-key" },
                "wallhaven": { "apiKey": "  spaced-key  " }
              }
            }
            """);

        var configuration = CreateConfiguration();

        Assert.Equal("file-key", configuration.GetApiKey("nasa"));
        Assert.Equal("spaced-key", configuration.GetApiKey("wallhaven"));
        Assert.Null(configuration.GetApiKey("unsplash"));
    }

    [Fact]
    public void GetApiKey_EnvironmentVariableWins()
    {
        File.WriteAllText(_filePath, """{ "providers": { "envtest": { "apiKey": "file-key" } } }""");
        Environment.SetEnvironmentVariable("MURALIS_PROVIDER_ENVTEST_API_KEY", "env-key");

        try
        {
            var configuration = CreateConfiguration();

            Assert.Equal("env-key", configuration.GetApiKey("envtest"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MURALIS_PROVIDER_ENVTEST_API_KEY", null);
        }
    }

    [Fact]
    public void GetApiKey_WithMalformedFile_ReturnsNull()
    {
        File.WriteAllText(_filePath, "{ not json at all ");
        var configuration = CreateConfiguration();

        Assert.Null(configuration.GetApiKey("nasa"));
    }

    [Fact]
    public void GetApiKey_PicksUpFileChangesWithoutRestart()
    {
        File.WriteAllText(_filePath, """{ "providers": { "nasa": { "apiKey": "first" } } }""");
        var configuration = CreateConfiguration();
        Assert.Equal("first", configuration.GetApiKey("nasa"));

        File.WriteAllText(_filePath, """{ "providers": { "nasa": { "apiKey": "second" } } }""");
        File.SetLastWriteTimeUtc(_filePath, DateTime.UtcNow.AddSeconds(1));

        Assert.Equal("second", configuration.GetApiKey("nasa"));
    }

    private ProviderConfiguration CreateConfiguration() =>
        new(NullLogger<ProviderConfiguration>.Instance, _filePath);
}
