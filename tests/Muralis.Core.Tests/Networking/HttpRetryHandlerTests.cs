using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Networking;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Networking;

public sealed class HttpRetryHandlerTests
{
    [Fact]
    public async Task TransientFailures_AreRetriedUntilSuccess()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(inner);

        var response = await client.GetAsync("https://example.test/feed");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ClientErrors_AreNotRetried()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = CreateClient(inner);

        var response = await client.GetAsync("https://example.test/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PersistentFailures_StopAfterTheRetryBudget()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        });
        using var client = CreateClient(inner);

        var response = await client.GetAsync("https://example.test/down");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Throttling_IsRetried()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
            return response;
        });
        using var client = CreateClient(inner);

        await client.GetAsync("https://example.test/limited");

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CallerCancellation_IsNotRetried()
    {
        var attempts = 0;
        var inner = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var client = CreateClient(inner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("https://example.test/feed", cancellation.Token));
        Assert.Equal(0, attempts);
    }

    private static HttpClient CreateClient(HttpMessageHandler inner) =>
        new(new HttpRetryHandler(NullLogger<HttpRetryHandler>.Instance, inner));
}
