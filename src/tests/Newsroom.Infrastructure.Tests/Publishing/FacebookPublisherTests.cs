using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Newsroom.Core.Publishing;
using Newsroom.Infrastructure.Publishing;

namespace Newsroom.Infrastructure.Tests.Publishing;

public class FacebookPublisherTests
{
    private const string FeedPath = "/v23.0/page-1/feed";
    private const string PostPath = "/v23.0/page-1_post-9";

    private const string PostedJson = """{"id":"page-1_post-9"}""";
    private const string PermalinkJson =
        """{"permalink_url":"https://www.facebook.com/page-1/posts/post-9","id":"page-1_post-9"}""";

    private static FacebookOptions Options => new()
    {
        PageId = "page-1",
        AccessToken = "tok-fb",
        GraphVersion = "v23.0",
        DryRun = false,
    };

    private static FacebookPost Post(long draftId = 42) => new(
        draftId, "Заглавие на новината", "Кратко резюме на статията.",
        "https://predel.news/novini/zaglavie");

    private static (FacebookPublisher Publisher, RoutingHandler Handler) CreatePublisher(
        Func<RecordedRequest, HttpResponseMessage> respond, FacebookOptions? options = null)
    {
        var handler = new RoutingHandler(respond);
        var publisher = new FacebookPublisher(
            new HttpClient(handler), options ?? Options, NullLogger<FacebookPublisher>.Instance);
        return (publisher, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>application/x-www-form-urlencoded body → decoded key/value pairs.</summary>
    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));

    [Fact]
    public async Task Posts_the_link_post_as_form_fields_with_the_composed_message()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == FeedPath
            ? Json(HttpStatusCode.OK, PostedJson)
            : Json(HttpStatusCode.OK, PermalinkJson));

        var result = await publisher.PublishAsync(Post(), CancellationToken.None);

        Assert.Equal("page-1_post-9", result.PostId);

        var feed = Assert.Single(handler.Requests, r => r.Path == FeedPath);
        var form = ParseForm(feed.Body);
        Assert.Equal("Заглавие на новината\n\nКратко резюме на статията.", form["message"]);
        Assert.Equal("https://predel.news/novini/zaglavie", form["link"]);
        Assert.Equal("tok-fb", form["access_token"]);
    }

    [Fact]
    public async Task Omits_the_link_when_IncludeLink_is_off()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == FeedPath
            ? Json(HttpStatusCode.OK, PostedJson)
            : Json(HttpStatusCode.OK, PermalinkJson),
            Options with { IncludeLink = false });

        var result = await publisher.PublishAsync(Post(), CancellationToken.None);

        Assert.Equal("page-1_post-9", result.PostId);
        var feed = Assert.Single(handler.Requests, r => r.Path == FeedPath);
        var form = ParseForm(feed.Body);
        Assert.Equal("Заглавие на новината\n\nКратко резюме на статията.", form["message"]);
        Assert.False(form.ContainsKey("link"));
    }

    [Fact]
    public async Task Omits_the_link_when_the_article_url_is_empty()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == FeedPath
            ? Json(HttpStatusCode.OK, PostedJson)
            : Json(HttpStatusCode.OK, PermalinkJson));

        await publisher.PublishAsync(Post() with { ArticleUrl = "" }, CancellationToken.None);

        var feed = Assert.Single(handler.Requests, r => r.Path == FeedPath);
        Assert.False(ParseForm(feed.Body).ContainsKey("link"));
    }

    [Fact]
    public async Task Fetches_the_permalink_after_posting()
    {
        var (publisher, handler) = CreatePublisher(request => request.Path == FeedPath
            ? Json(HttpStatusCode.OK, PostedJson)
            : Json(HttpStatusCode.OK, PermalinkJson));

        var result = await publisher.PublishAsync(Post(), CancellationToken.None);

        Assert.Equal("https://www.facebook.com/page-1/posts/post-9", result.PermalinkUrl);
        var permalink = Assert.Single(handler.Requests, r => r.Path == PostPath);
        Assert.Contains("fields=permalink_url", permalink.Query);
        Assert.Contains("access_token=tok-fb", permalink.Query);
    }

    [Fact]
    public async Task A_failed_permalink_fetch_is_tolerated()
    {
        var (publisher, _) = CreatePublisher(request => request.Path == FeedPath
            ? Json(HttpStatusCode.OK, PostedJson)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await publisher.PublishAsync(Post(), CancellationToken.None);

        Assert.Equal("page-1_post-9", result.PostId);
        Assert.Null(result.PermalinkUrl);
    }

    [Fact]
    public async Task An_invalid_token_error_190_is_a_rejection_that_names_the_token()
    {
        var (publisher, _) = CreatePublisher(_ => Json(HttpStatusCode.BadRequest,
            """
            {"error":{"message":"Error validating access token: Session has expired",
             "type":"OAuthException","code":190}}
            """));

        var ex = await Assert.ThrowsAsync<PublishRejectedException>(
            () => publisher.PublishAsync(Post(), CancellationToken.None));

        Assert.StartsWith(FacebookPublisher.TokenInvalidPrefix, ex.Message);
        Assert.Contains("Session has expired", ex.Message);
    }

    [Fact]
    public async Task An_OAuthException_without_code_190_still_names_the_token()
    {
        var (publisher, _) = CreatePublisher(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid OAuth access token.","type":"OAuthException","code":102}}"""));

        var ex = await Assert.ThrowsAsync<PublishRejectedException>(
            () => publisher.PublishAsync(Post(), CancellationToken.None));

        Assert.StartsWith(FacebookPublisher.TokenInvalidPrefix, ex.Message);
    }

    [Fact]
    public async Task A_described_Graph_error_surfaces_its_message_as_a_rejection()
    {
        var (publisher, _) = CreatePublisher(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid parameter","type":"GraphMethodException","code":100}}"""));

        var ex = await Assert.ThrowsAsync<PublishRejectedException>(
            () => publisher.PublishAsync(Post(), CancellationToken.None));

        Assert.Equal("Invalid parameter", ex.Message);
    }

    [Fact]
    public async Task A_server_error_stays_a_transient_HttpRequestException()
    {
        var (publisher, _) = CreatePublisher(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => publisher.PublishAsync(Post(), CancellationToken.None));
    }

    [Fact]
    public async Task A_4xx_without_a_Graph_error_body_stays_transient()
    {
        var (publisher, _) = CreatePublisher(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => publisher.PublishAsync(Post(), CancellationToken.None));
    }

    [Fact]
    public async Task Dry_run_posts_nothing_and_reports_a_dryrun_post_id()
    {
        var (publisher, handler) = CreatePublisher(
            _ => Json(HttpStatusCode.OK, PostedJson), Options with { DryRun = true });

        var result = await publisher.PublishAsync(Post(), CancellationToken.None);

        Assert.Equal(FacebookPublisher.DryRunPostId, result.PostId);
        Assert.Null(result.PermalinkUrl);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_token_check_is_true_on_200()
    {
        var (publisher, handler) = CreatePublisher(
            _ => Json(HttpStatusCode.OK, """{"id":"page-1"}"""));

        Assert.True(await publisher.CheckTokenAsync(CancellationToken.None));
        var check = Assert.Single(handler.Requests);
        Assert.Equal("/v23.0/page-1", check.Path);
        Assert.Contains("fields=id", check.Query);
    }

    [Fact]
    public async Task The_token_check_is_false_on_an_error_response()
    {
        var (publisher, _) = CreatePublisher(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}"""));

        Assert.False(await publisher.CheckTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_token_check_never_throws_even_when_the_network_does()
    {
        var (publisher, _) = CreatePublisher(
            _ => throw new HttpRequestException("connection refused"));

        Assert.False(await publisher.CheckTokenAsync(CancellationToken.None));
    }

    /// <summary>What one request looked like on the wire — captured at send time, because the
    /// publisher disposes its requests after use.</summary>
    private sealed record RecordedRequest(string Path, string Query, string Body);

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
                request.RequestUri!.Query,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(recorded);
            return respond(recorded);
        }
    }
}
