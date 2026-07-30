using System.Net;
using System.Text;

namespace Newsroom.Infrastructure.Tests.Images;

/// <summary>Hands out HttpClients over a canned-response handler — no network in tests.</summary>
internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Returns one canned JSON response and records the request it answered. The body is
/// captured at send time (LastRequestBody) — callers dispose the request after sending.</summary>
internal sealed class CannedResponseHandler(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestCount++;
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
