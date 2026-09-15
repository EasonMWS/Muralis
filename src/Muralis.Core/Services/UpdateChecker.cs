using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muralis.Core.Abstractions;
using Muralis.Core.Helpers;
using Muralis.Core.Models;

namespace Muralis.Core.Services;

/// <summary>
/// Checks the release feed for a newer build. The unauthenticated GitHub API allows only a
/// low request rate, so the response is cached on disk and the endpoint is asked again at
/// most every <see cref="CacheLifetime"/>, no matter how often the user checks.
/// </summary>
public sealed class UpdateChecker : IUpdateChecker
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly string _releasesUrl;
    private readonly ILogger<UpdateChecker> _logger;

    /// <param name="releasesUrl">Latest-release endpoint of the project's GitHub repository.</param>
    public UpdateChecker(HttpClient httpClient, string releasesUrl, ILogger<UpdateChecker> logger)
    {
        _httpClient = httpClient;
        _releasesUrl = releasesUrl;
        _logger = logger;
    }

    public async Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var release = await RemoteJson
            .GetAsync<GitHubRelease>(_httpClient, _releasesUrl, CacheLifetime, _logger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var latest = ParseVersion(release?.TagName);
        if (latest is null)
        {
            _logger.LogWarning("Update check found no readable release tag");
            return null;
        }

        var current = ParseVersion(currentVersion) ?? new Version(0, 0);
        var available = latest > current;
        _logger.LogInformation(
            "Update check: running {Current}, latest {Latest}, update available: {Available}",
            currentVersion,
            release!.TagName,
            available);

        return new UpdateCheckResult(available, release.TagName!.Trim().TrimStart('v', 'V'), release.HtmlUrl);
    }

    /// <summary>Reads a numeric version out of a release tag such as "v0.2.0".</summary>
    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().TrimStart('v', 'V');
        var suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
