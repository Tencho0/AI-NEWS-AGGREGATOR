using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Newsroom.Core.Scraping;
using Newsroom.Infrastructure.Scraping;

namespace Newsroom.Infrastructure.Tests.Scraping;

public class RssFeedReaderTests
{
    private static readonly Source TestSource = new()
    {
        Id = 1,
        Name = "Тест",
        Kind = SourceKind.Rss,
        Url = "https://example.com/rss",
    };

    private static RssFeedReader CreateReader(HttpResponseMessage response, Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new StubHandler(response, onRequest);
        return new RssFeedReader(new HttpClient(handler), NullLogger<RssFeedReader>.Instance);
    }

    private static HttpResponseMessage FixtureResponse()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-feed.xml"));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
        return response;
    }

    [Fact]
    public async Task Parses_items_and_skips_linkless_entries()
    {
        var result = await CreateReader(FixtureResponse()).FetchAsync(TestSource, CancellationToken.None);

        Assert.False(result.NotModified);
        Assert.Equal(2, result.Items.Count); // third item has no link → skipped
        Assert.Equal("\"v1\"", result.Etag);

        var first = result.Items[0];
        Assert.Equal("Ремонтът на централния площад приключи", first.Title);
        Assert.StartsWith("https://example.com/novini/remont-ploshtad", first.Link);
        Assert.NotNull(first.Text);
        Assert.Contains("два месеца предсрочно", first.Text);   // content:encoded preferred
        Assert.DoesNotContain("<p>", first.Text);               // markup stripped
        Assert.Equal(new DateTime(2026, 7, 1, 5, 30, 0), first.PublishedAtUtc); // +03:00 → UTC

        var second = result.Items[1];
        Assert.Equal("Съвсем кратко резюме без пълен текст.", second.Text); // summary fallback
    }

    [Fact]
    public async Task Sends_conditional_headers_and_handles_304()
    {
        HttpRequestMessage? seen = null;
        var reader = CreateReader(
            new HttpResponseMessage(HttpStatusCode.NotModified),
            request => seen = request);

        var source = TestSource with { Etag = "\"v1\"", LastModifiedHeader = "Wed, 01 Jul 2026 05:30:00 GMT" };
        var result = await reader.FetchAsync(source, CancellationToken.None);

        Assert.True(result.NotModified);
        Assert.Empty(result.Items);
        Assert.Equal("\"v1\"", result.Etag); // caching state preserved
        Assert.NotNull(seen);
        Assert.Equal("\"v1\"", seen!.Headers.IfNoneMatch.ToString());
        Assert.NotNull(seen.Headers.IfModifiedSince);
    }

    [Fact]
    public async Task Non_success_status_throws_for_source_level_failure_handling()
    {
        var reader = CreateReader(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.FetchAsync(TestSource, CancellationToken.None));
    }

    private sealed class StubHandler(HttpResponseMessage response, Action<HttpRequestMessage>? onRequest)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke(request);
            return Task.FromResult(response);
        }
    }
}
