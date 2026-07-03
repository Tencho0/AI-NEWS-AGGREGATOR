using System.Net;
using System.Text;
using System.Text.Json;

using Newsroom.Core.Publishing;
using Newsroom.Infrastructure.Publishing;

namespace Newsroom.Infrastructure.Tests.Publishing;

public class UmbracoPublisherTests
{
    private const string TokenPath = "/umbraco/management/api/v1/security/back-office/token";
    private const string ArticlesPath = "/umbraco/management/api/v1/predelnews/publishing/articles";

    private const string TokenJson = """{"access_token":"tok-1","expires_in":3600}""";
    private const string PublishedJson =
        """
        {"contentKey":"6f9619ff-8b86-d011-b42d-00cf4fc964ff",
         "url":"https://predel.news/novini/zaglavie","alreadyExisted":false}
        """;

    private static UmbracoOptions Options => new()
    {
        BaseUrl = "https://predel.news/",
        ClientId = "newsroom-bot",
        ClientSecret = "s3cret",
    };

    private static ArticleToPublish Article(long draftId = 42) => new(
        draftId, "Заглавие", "Подзаглавие", "Тяло на статията.", "Общество", "Петрич",
        ["Петрич", "новини"], "SEO заглавие", "SEO описание",
        new PublishImage("photo.jpg", "https://images.example/photo.jpg",
            "Площадът в Петрич", "Pexels / John"));

    private static (UmbracoPublisher Publisher, RoutingHandler Handler) CreatePublisher(
        Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new RoutingHandler(respond);
        return (new UmbracoPublisher(new HttpClient(handler), Options), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Acquires_a_token_via_client_credentials_then_publishes()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : Json(HttpStatusCode.OK, PublishedJson));

        var result = await publisher.PublishAsync(Article(), CancellationToken.None);

        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"), result.ContentKey);
        Assert.Equal("https://predel.news/novini/zaglavie", result.Url);
        Assert.False(result.AlreadyExisted);

        var token = Assert.Single(handler.Requests, r => r.Path == TokenPath);
        Assert.Contains("grant_type=client_credentials", token.Body);
        Assert.Contains("client_id=umbraco-back-office-newsroom-bot", token.Body);
        Assert.Contains("client_secret=s3cret", token.Body);

        var publish = Assert.Single(handler.Requests, r => r.Path == ArticlesPath);
        Assert.Equal("tok-1", publish.Bearer);
    }

    [Fact]
    public async Task The_request_json_is_camelCase_and_carries_the_contract_fields()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : Json(HttpStatusCode.OK, PublishedJson));

        await publisher.PublishAsync(Article(), CancellationToken.None);

        var publish = Assert.Single(handler.Requests, r => r.Path == ArticlesPath);
        using var body = JsonDocument.Parse(publish.Body);
        var root = body.RootElement;

        Assert.Equal("newsroom-draft-42", root.GetProperty("externalRef").GetString());
        Assert.Equal("Заглавие", root.GetProperty("headline").GetString());
        Assert.Equal("Подзаглавие", root.GetProperty("subtitle").GetString());
        Assert.Equal("Тяло на статията.", root.GetProperty("bodyMarkdown").GetString());
        Assert.Equal("Общество", root.GetProperty("category").GetString());
        Assert.Equal("Петрич", root.GetProperty("region").GetString());
        Assert.Equal(2, root.GetProperty("tags").GetArrayLength());
        Assert.Equal("SEO заглавие", root.GetProperty("seoTitle").GetString());
        Assert.Equal("SEO описание", root.GetProperty("seoDescription").GetString());
        Assert.True(root.TryGetProperty("publishDateUtc", out var publishDate));
        Assert.EndsWith("Z", publishDate.GetString()); // ISO 8601, UTC

        var image = root.GetProperty("image");
        Assert.Equal("photo.jpg", image.GetProperty("fileName").GetString());
        Assert.Equal("https://images.example/photo.jpg", image.GetProperty("sourceUrl").GetString());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("bytesBase64").ValueKind);
        Assert.Equal("Площадът в Петрич", image.GetProperty("altText").GetString());
        Assert.Equal("Pexels / John", image.GetProperty("attribution").GetString());
    }

    [Fact]
    public async Task An_article_without_an_image_sends_a_null_image()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : Json(HttpStatusCode.OK, PublishedJson));

        await publisher.PublishAsync(Article() with { Image = null }, CancellationToken.None);

        var publish = Assert.Single(handler.Requests, r => r.Path == ArticlesPath);
        using var body = JsonDocument.Parse(publish.Body);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("image").ValueKind);
    }

    [Fact]
    public async Task The_token_is_cached_across_publishes()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : Json(HttpStatusCode.OK, PublishedJson));

        await publisher.PublishAsync(Article(1), CancellationToken.None);
        await publisher.PublishAsync(Article(2), CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Path == TokenPath);
        Assert.Equal(2, handler.Requests.Count(r => r.Path == ArticlesPath));
    }

    [Fact]
    public async Task A_400_problem_becomes_a_PublishRejectedException_with_the_detail()
    {
        var (publisher, _) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : Json(HttpStatusCode.BadRequest,
                """{"title":"Validation failed","detail":"Категория 'Х' не съществува.","status":400}"""));

        var ex = await Assert.ThrowsAsync<PublishRejectedException>(
            () => publisher.PublishAsync(Article(), CancellationToken.None));

        Assert.Equal("Категория 'Х' не съществува.", ex.Message);
    }

    [Fact]
    public async Task A_400_problem_without_detail_falls_back_to_the_title()
    {
        var (publisher, _) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : Json(HttpStatusCode.BadRequest, """{"title":"Payload too large","status":400}"""));

        var ex = await Assert.ThrowsAsync<PublishRejectedException>(
            () => publisher.PublishAsync(Article(), CancellationToken.None));

        Assert.Equal("Payload too large", ex.Message);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_and_retries_once()
    {
        var tokensIssued = 0;
        var (publisher, handler) = CreatePublisher(request =>
        {
            if (request.Path == TokenPath)
            {
                tokensIssued++;
                return Json(HttpStatusCode.OK,
                    $$"""{"access_token":"tok-{{tokensIssued}}","expires_in":3600}""");
            }
            return request.Bearer == "tok-2"
                ? Json(HttpStatusCode.OK, PublishedJson)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var result = await publisher.PublishAsync(Article(), CancellationToken.None);

        Assert.Equal("https://predel.news/novini/zaglavie", result.Url);
        Assert.Equal(2, handler.Requests.Count(r => r.Path == TokenPath));
        Assert.Equal("tok-2", handler.Requests.Last(r => r.Path == ArticlesPath).Bearer);
    }

    [Fact]
    public async Task A_persistent_401_gives_up_after_one_retry()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => publisher.PublishAsync(Article(), CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count(r => r.Path == ArticlesPath));
    }

    [Fact]
    public async Task A_server_error_stays_a_transient_HttpRequestException()
    {
        var (publisher, _) = CreatePublisher(request => request.Path == TokenPath
            ? Json(HttpStatusCode.OK, TokenJson)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => publisher.PublishAsync(Article(), CancellationToken.None));
    }

    /// <summary>What one request looked like on the wire — captured at send time, because the
    /// publisher disposes its requests after use.</summary>
    private sealed record RecordedRequest(string Path, string? Bearer, string Body);

    /// <summary>Routes each request to a scripted response and records it — no network.</summary>
    private sealed class RoutingHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Parameter,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(recorded);
            return respond(recorded);
        }
    }
}
