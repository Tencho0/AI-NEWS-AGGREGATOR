using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;

namespace Newsroom.Infrastructure.Images;

/// <summary>The Cloudflare Workers AI call failed — HTTP error, exhausted free quota (429),
/// or a malformed payload. Always recoverable by falling back to the stock providers.</summary>
public sealed class CloudflareAiException(string message) : Exception(message);

/// <summary>
/// <see cref="IAiImageGenerator"/> over the Cloudflare Workers AI REST API running FLUX.1
/// Schnell (docs/05-integrations/images.md tier 3, ADR-0011). The response carries the image
/// as base64 JPEG; it is saved under <see cref="CloudflareImagesOptions.GeneratedImageDir"/>
/// and the candidate's Url is that worker-local path — published exactly like an editor
/// upload (inlined as base64 to the site, multipart to Facebook). Every failure throws
/// <see cref="CloudflareAiException"/> with the provider's error text; the caller
/// (FeaturedImageService) falls back to the stock providers.
/// </summary>
public sealed class CloudflareFluxImageGenerator(
    IHttpClientFactory httpClientFactory, CloudflareImagesOptions options) : IAiImageGenerator
{
    /// <summary>Named HttpClient with a generation-friendly timeout and no retry — a failed
    /// generation falls back to stock instead of burning more quota on retries.</summary>
    public const string HttpClientName = "cloudflare-ai";

    /// <summary>Caption per docs/05-integrations/images.md tier 3 — the site shows the
    /// attribution as the image credit, so readers see the image is an illustration.</summary>
    public const string IllustrationAttribution = "Илюстрация";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string generatedImageDir = Path.IsPathRooted(options.GeneratedImageDir)
        ? options.GeneratedImageDir
        : Path.Combine(AppContext.BaseDirectory, options.GeneratedImageDir);

    public string Name => "Cloudflare FLUX.1 Schnell";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.AccountId) && !string.IsNullOrWhiteSpace(options.ApiToken);

    public async Task<AiImageResult> GenerateAsync(DraftContent content, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new CloudflareAiException("Cloudflare Workers AI is not configured (Images:Cloudflare:AccountId / ApiToken).");

        var prompt = FluxPromptComposer.Compose(content);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.cloudflare.com/client/v4/accounts/{options.AccountId}/ai/run/{options.Model}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.ApiToken}");
        request.Content = JsonContent.Create(
            new FluxRequest(prompt, options.Steps, options.Width, options.Height), options: JsonOptions);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new CloudflareAiException(
                $"Cloudflare Workers AI quota/rate limit exhausted (HTTP 429): {FirstErrorMessage(body)}");
        if (!response.IsSuccessStatusCode)
            throw new CloudflareAiException(
                $"Cloudflare Workers AI returned HTTP {(int)response.StatusCode}: {FirstErrorMessage(body)}");

        var imageBase64 = ParseImage(body);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(imageBase64);
        }
        catch (FormatException)
        {
            throw new CloudflareAiException("Cloudflare Workers AI returned an image that is not valid base64.");
        }
        if (bytes.Length == 0)
            throw new CloudflareAiException("Cloudflare Workers AI returned an empty image.");

        var localPath = await SaveAsync(bytes, ct).ConfigureAwait(false);
        var image = new ImageCandidate(
            localPath,
            ThumbUrl: null,
            Name,
            IllustrationAttribution,
            options.Width,
            options.Height,
            ImageSourceKinds.Ai);
        // Cost 0 on the free tier, but the ledger row still counts against
        // Ai:Stages:Image:DailyRequestBudget and shows up in /quota (ADR-0010 metering).
        return new AiImageResult(image, new AiUsage("Cloudflare", options.Model, TokensIn: 0, TokensOut: 0, Cost: 0));
    }

    /// <summary>The REST envelope is {"result":{"image":"<base64 JPEG>"},"success":true,...};
    /// success:false with HTTP 200 still happens for some validation errors.</summary>
    private static string ParseImage(string body)
    {
        FluxEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FluxEnvelope>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new CloudflareAiException("Cloudflare Workers AI returned a non-JSON response.");
        }

        if (envelope is null || !envelope.Success)
            throw new CloudflareAiException(
                $"Cloudflare Workers AI reported failure: {FirstErrorMessage(body)}");
        if (string.IsNullOrWhiteSpace(envelope.Result?.Image))
            throw new CloudflareAiException("Cloudflare Workers AI returned no image.");
        return envelope.Result.Image;
    }

    private async Task<string> SaveAsync(byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(generatedImageDir);
        var fileName = $"flux-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jpg";
        var path = Path.GetFullPath(Path.Combine(generatedImageDir, fileName));
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>The first entry of the envelope's errors array ("code: message"), falling back
    /// to the raw body — this text ends up in the fallback warning log.</summary>
    private static string FirstErrorMessage(string body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<FluxEnvelope>(body, JsonOptions);
            if (envelope?.Errors is { Count: > 0 } errors)
                return string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"));
        }
        catch (JsonException)
        {
            // not JSON — fall through to the raw body
        }
        return string.IsNullOrWhiteSpace(body) ? "(empty response body)" : Truncate(body, 300);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>Wire shape of the generation request (verified live 2026-07-30: width/height
    /// are honoured even though the public schema only documents prompt/steps/seed).</summary>
    private sealed record FluxRequest(string Prompt, int Steps, int Width, int Height);

    /// <summary>Wire shape of the REST envelope (fields we use only).</summary>
    private sealed record FluxEnvelope(FluxResult? Result, bool Success, List<FluxError>? Errors);

    private sealed record FluxResult(string? Image);

    private sealed record FluxError(int Code, string? Message);
}
