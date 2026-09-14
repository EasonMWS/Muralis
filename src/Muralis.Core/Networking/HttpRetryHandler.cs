using Microsoft.Extensions.Logging;

namespace Muralis.Core.Networking;

/// <summary>
/// Retries transient failures of idempotent (GET) requests with exponential backoff and
/// jitter: connection errors, timeouts, 408, 429 and 5xx responses. A <c>Retry-After</c>
/// header from the server is honoured. The caller's cancellation token always wins.
/// </summary>
public sealed class HttpRetryHandler : DelegatingHandler
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger<HttpRetryHandler> _logger;

    public HttpRetryHandler(ILogger<HttpRetryHandler> logger) => _logger = logger;

    public HttpRetryHandler(ILogger<HttpRetryHandler> logger, HttpMessageHandler innerHandler)
        : base(innerHandler) => _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (attempt >= MaxAttempts || request.Method != HttpMethod.Get || !IsTransient(response.StatusCode))
                {
                    return response;
                }

                var delay = GetDelay(attempt, response);
                _logger.LogWarning(
                    "Transient HTTP {Status} from {Host}; retrying in {Delay} ms (attempt {Attempt}/{MaxAttempts})",
                    (int)response.StatusCode,
                    request.RequestUri?.Host,
                    (int)delay.TotalMilliseconds,
                    attempt,
                    MaxAttempts);

                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                attempt < MaxAttempts
                && request.Method == HttpMethod.Get
                && !cancellationToken.IsCancellationRequested
                && IsTransient(ex))
            {
                var delay = GetDelay(attempt, null);
                _logger.LogWarning(
                    ex,
                    "Transient HTTP failure from {Host}; retrying in {Delay} ms (attempt {Attempt}/{MaxAttempts})",
                    request.RequestUri?.Host,
                    (int)delay.TotalMilliseconds,
                    attempt,
                    MaxAttempts);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or TimeoutException or IOException;

    private static TimeSpan GetDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            {
                return delta > MaxDelay ? MaxDelay : delta;
            }

            if (retryAfter.Date is { } date && date - DateTimeOffset.UtcNow > TimeSpan.Zero)
            {
                var wait = date - DateTimeOffset.UtcNow;
                return wait > MaxDelay ? MaxDelay : wait;
            }
        }

        var backoff = TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 200));
        var delay = backoff + jitter;
        return delay > MaxDelay ? MaxDelay : delay;
    }
}
