using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Newsroom.Core.Publishing;

namespace Newsroom.Infrastructure.Publishing;

/// <summary>
/// <see cref="IFacebookPublisher"/> over the Graph API (docs/05-integrations/facebook.md,
/// ADR-0008): POST /{page-id}/feed with message + link, then a best-effort permalink fetch.
/// Graph errors arrive as 4xx with {"error":{message,type,code}}: token errors (code 190 /
/// OAuthException) and any other described 4xx become <see cref="PublishRejectedException"/>
/// (permanent — the post itself is refused); 5xx and undescribed failures stay ordinary
/// <see cref="HttpRequestException"/> (transient — PublishJob retries next cycles). In dry-run
/// mode (Facebook:DryRun, default ON) nothing is sent — the would-be post is only logged.
/// </summary>
public sealed class FacebookPublisher(
    HttpClient http, FacebookOptions options, ILogger<FacebookPublisher> logger) : IFacebookPublisher
{
    /// <summary>Marker prefix on token rejections, so PublishJob can flag token health in
    /// addition to recording the terminal failure.</summary>
    public const string TokenInvalidPrefix = "Facebook token invalid/expired";

    /// <summary>Post id reported for dry-run "posts" (never sent to Facebook).</summary>
    public const string DryRunPostId = "dryrun";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FacebookPostResult> PublishAsync(FacebookPost post, CancellationToken ct)
    {
        var message = $"{post.Headline}\n\n{post.Teaser}";
        // The link (→ OG card) is included only when configured and present; otherwise it is a
        // plain text post that does not carry the site URL (Facebook:IncludeLink).
        var withLink = options.IncludeLink && !string.IsNullOrWhiteSpace(post.ArticleUrl);
        if (options.DryRun)
        {
            logger.LogInformation(
                "Facebook dry run for draft {DraftId} — would post to page {PageId}:\n{Message}{Link}",
                post.DraftId, options.PageId, message, withLink ? $"\n{post.ArticleUrl}" : "");
            return new FacebookPostResult(DryRunPostId, null);
        }

        var form = new Dictionary<string, string>
        {
            ["message"] = message,
            ["access_token"] = options.AccessToken,
        };
        if (withLink)
            form["link"] = post.ArticleUrl;

        using var response = await http.PostAsync(
            Endpoint($"{options.PageId}/feed"), new FormUrlEncodedContent(form), ct);
        await ThrowIfGraphErrorAsync(response, ct);

        var created = await response.Content.ReadFromJsonAsync<FeedResponse>(JsonOptions, ct);
        if (created?.Id is not { } postId || string.IsNullOrWhiteSpace(postId))
            throw new HttpRequestException("The Graph API returned no post id.");
        return new FacebookPostResult(postId, await TryGetPermalinkAsync(postId, ct));
    }

    public async Task<bool> CheckTokenAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                Endpoint($"{options.PageId}?fields=id&access_token={Uri.EscapeDataString(options.AccessToken)}"),
                ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // health checks report, never throw
        }
    }

    /// <summary>Best-effort: a post without a permalink is still a successful post — the
    /// editor gets the post id instead of a clickable link.</summary>
    private async Task<string?> TryGetPermalinkAsync(string postId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                Endpoint($"{Uri.EscapeDataString(postId)}?fields=permalink_url"
                    + $"&access_token={Uri.EscapeDataString(options.AccessToken)}"),
                ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var payload = await response.Content.ReadFromJsonAsync<PermalinkResponse>(JsonOptions, ct);
            return string.IsNullOrWhiteSpace(payload?.PermalinkUrl) ? null : payload!.PermalinkUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch the permalink for Facebook post {PostId}", postId);
            return null;
        }
    }

    private Uri Endpoint(string pathAndQuery) =>
        new($"https://graph.facebook.com/{options.GraphVersion}/{pathAndQuery}");

    /// <summary>Maps a non-success response: a described 4xx is a permanent rejection — code
    /// 190 / OAuthException means the page token itself is dead and gets the
    /// <see cref="TokenInvalidPrefix"/> marker; everything else (5xx, no error body) stays a
    /// transient <see cref="HttpRequestException"/>.</summary>
    private static async Task ThrowIfGraphErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        if ((int)response.StatusCode is >= 400 and < 500
            && await ReadGraphErrorAsync(response, ct) is { } error)
        {
            var reason = string.IsNullOrWhiteSpace(error.Message)
                ? $"The Graph API rejected the post (HTTP {(int)response.StatusCode})."
                : error.Message!;
            throw error.Code == 190 || error.Type == "OAuthException"
                ? new PublishRejectedException($"{TokenInvalidPrefix}: {reason}")
                : new PublishRejectedException(reason);
        }

        response.EnsureSuccessStatusCode(); // throws HttpRequestException
    }

    private static async Task<GraphError?> ReadGraphErrorAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<GraphErrorEnvelope>(JsonOptions, ct);
            return envelope?.Error;
        }
        catch (JsonException)
        {
            return null; // not the documented error shape — treat as transient
        }
    }

    /// <summary>Wire shape of POST /{page-id}/feed: {"id":"{page-id}_{post-id}"}.</summary>
    private sealed record FeedResponse(string? Id);

    /// <summary>Wire shape of GET /{post-id}?fields=permalink_url.</summary>
    private sealed record PermalinkResponse(
        [property: JsonPropertyName("permalink_url")] string? PermalinkUrl);

    /// <summary>Wire shape of Graph API errors: {"error":{"message","type","code"}}.</summary>
    private sealed record GraphErrorEnvelope(GraphError? Error);

    private sealed record GraphError(string? Message, string? Type, int Code);
}
