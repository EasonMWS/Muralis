using System.Net;
using System.Text;

namespace Muralis.Core.Tests.TestSupport;

/// <summary>Deterministic HTTP handler used to test providers, downloads and caching.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

    public int RequestCount { get; private set; }

    public List<string> RequestedUrls { get; } = [];

    public static FakeHttpMessageHandler Json(string json) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public static FakeHttpMessageHandler Bytes(byte[] data, string contentType = "image/jpeg") =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data) is { } content && SetType(content, contentType) ? content : throw new InvalidOperationException(),
        });

    public static FakeHttpMessageHandler Status(HttpStatusCode status) =>
        new(_ => new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        RequestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_handler(request));
    }

    private static bool SetType(ByteArrayContent content, string contentType)
    {
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return true;
    }
}
