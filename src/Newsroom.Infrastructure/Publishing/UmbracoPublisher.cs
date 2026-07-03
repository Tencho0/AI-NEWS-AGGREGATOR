using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Newsroom.Core.Publishing;

namespace Newsroom.Infrastructure.Publishing;

/// <summary>
/// <see cref="IUmbracoPublisher"/> over the site's publishing endpoint
/// (docs/05-integrations/umbraco.md, ADR-0007). Authenticates via OAuth2 client credentials
/// as the dedicated "newsroom-bot" API user; the bearer token is cached until 60 s before
/// expiry (thread-safe) and refreshed once when a publish comes back 401 (revoked
/// server-side). HTTP 400 problem details become <see cref="PublishRejectedException"/>
/// (permanent — the payload itself is refused); everything else non-success stays an ordinary
/// <see cref="HttpRequestException"/> (transient — PublishJob retries next cycles).
/// </summary>
public sealed class UmbracoPublisher(HttpClient http, UmbracoOptions options) : IUmbracoPublisher
{
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? cachedToken;
    private DateTime tokenExpiresUtc;

    public async Task<UmbracoPublishResult> PublishAsync(ArticleToPublish article, CancellationToken ct)
    {
        var response = await SendAsync(article, forceTokenRefresh: false, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The cached token was revoked or expired server-side: refresh and retry once.
            response.Dispose();
            response = await SendAsync(article, forceTokenRefresh: true, ct);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new PublishRejectedException(await ReadProblemMessageAsync(response, ct));
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<PublishResponse>(JsonOptions, ct);
            if (result?.Url is not { } url || string.IsNullOrWhiteSpace(url))
                throw new HttpRequestException("The publishing endpoint returned no article URL.");
            return new UmbracoPublishResult(result.ContentKey, url, result.AlreadyExisted);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        ArticleToPublish article, bool forceTokenRefresh, CancellationToken ct)
    {
        var token = await GetTokenAsync(forceTokenRefresh, ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, Endpoint("umbraco/management/api/v1/predelnews/publishing/articles"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(ToPayload(article), options: JsonOptions);
        return await http.SendAsync(request, ct);
    }

    /// <summary>The cached token, or a fresh one via the client-credentials grant. Serialized
    /// behind a lock so concurrent publishes cannot stampede the token endpoint.</summary>
    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            if (forceRefresh)
                cachedToken = null;
            if (cachedToken is not null && DateTime.UtcNow < tokenExpiresUtc - ExpirySafetyMargin)
                return cachedToken;

            // Umbraco 17 back-office token endpoint; client ids of API users are force-prefixed
            // with "umbraco-back-office-" by Umbraco (Constants.OAuthClientIds.BackOffice).
            using var response = await http.PostAsync(
                Endpoint("umbraco/management/api/v1/security/back-office/token"),
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = $"umbraco-back-office-{options.ClientId}",
                    ["client_secret"] = options.ClientSecret,
                }),
                ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct);
            if (payload?.AccessToken is not { } token || string.IsNullOrWhiteSpace(token))
                throw new HttpRequestException("The token endpoint returned no access_token.");

            cachedToken = token;
            tokenExpiresUtc = DateTime.UtcNow.AddSeconds(payload.ExpiresIn);
            return token;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private Uri Endpoint(string path) => new($"{options.BaseUrl.TrimEnd('/')}/{path}");

    private static PublishRequest ToPayload(ArticleToPublish article) => new(
        $"newsroom-draft-{article.DraftId}",
        article.Headline,
        article.Subtitle,
        article.BodyMarkdown,
        article.Category,
        article.Region,
        article.Tags,
        article.SeoTitle,
        article.SeoDescription,
        DateTime.UtcNow, // publish date is the moment of publication (no scheduling in v1)
        article.Image is null
            ? null
            : new ImagePayload(
                article.Image.FileName, BytesBase64: null, article.Image.SourceUrl,
                article.Image.AltText, article.Image.Attribution));

    /// <summary>Problem-details 'detail' (or 'title'), falling back to the raw body — this
    /// text reaches the editor in the failure alert, so favour the most human-readable part.</summary>
    private static async Task<string> ReadProblemMessageAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var problem = JsonDocument.Parse(body);
            if (problem.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (TryGetNonEmptyString(problem.RootElement, "detail", out var detail))
                    return detail;
                if (TryGetNonEmptyString(problem.RootElement, "title", out var title))
                    return title;
            }
        }
        catch (JsonException)
        {
            // not JSON — fall through to the raw body
        }
        return string.IsNullOrWhiteSpace(body)
            ? "The publishing endpoint rejected the article (HTTP 400)."
            : body;
    }

    private static bool TryGetNonEmptyString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var found)
            && found.ValueKind == JsonValueKind.String
            && found.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>Wire shape of the publish request (docs/05-integrations/umbraco.md).</summary>
    private sealed record PublishRequest(
        string ExternalRef,
        string Headline,
        string? Subtitle,
        string BodyMarkdown,
        string Category,
        string? Region,
        IReadOnlyList<string> Tags,
        string? SeoTitle,
        string? SeoDescription,
        DateTime PublishDateUtc,
        ImagePayload? Image);

    /// <summary>Wire shape of the optional cover image: exactly one of BytesBase64/SourceUrl
    /// is set — v1 always sends the stock provider URL, fetched server-side by the site.</summary>
    private sealed record ImagePayload(
        string FileName, string? BytesBase64, string? SourceUrl, string AltText, string? Attribution);

    /// <summary>Wire shape of the publish response.</summary>
    private sealed record PublishResponse(Guid ContentKey, string? Url, bool AlreadyExisted);

    /// <summary>Wire shape of the OAuth2 token response (snake_case per the RFC).</summary>
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
