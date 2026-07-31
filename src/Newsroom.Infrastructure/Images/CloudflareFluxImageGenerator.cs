using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Core.Images;

namespace Newsroom.Infrastructure.Images;

/// <summary>
/// The Cloudflare Workers AI call failed. Two flags separate the three outcomes the caller has to
/// treat differently (ADR-0013):
/// <list type="bullet">
/// <item><see cref="QuotaExhausted"/> — the free daily allocation is gone (error code 3036, or an
/// HTTP 402 spend wall). Generation stops for the day and the editor is told; the worker never
/// spends money to keep going.</item>
/// <item><see cref="Transient"/> — Cloudflare is temporarily out of capacity (code 3040). Retried
/// a bounded number of times, then this draft falls back to stock. **No** daily lock.</item>
/// <item>Neither — an ordinary failure (bad prompt, auth, malformed payload): stock for this
/// draft, full retry on the next one.</item>
/// </list>
/// </summary>
public sealed class CloudflareAiException(string message, bool quotaExhausted = false, bool transient = false)
    : Exception(message)
{
    public bool QuotaExhausted { get; } = quotaExhausted;

    public bool Transient { get; } = transient;
}

/// <summary>
/// <see cref="IAiImageGenerator"/> over the Cloudflare Workers AI REST API (ADR-0011, ADR-0012,
/// ADR-0013). The default model is FLUX.2 klein 4B, which takes <c>multipart/form-data</c> even for
/// a prompt-only request and accepts up to four reference images (<c>input_image_0..3</c>, each
/// smaller than 512×512) — that is what lets an approved public-figure photo reach the model. The
/// legacy JSON shape (FLUX.1 Schnell) is still available via
/// <see cref="CloudflareImagesOptions.RequestFormat"/> and never carries references.
///
/// The response carries the image as base64 JPEG. The real Predel News logo is composited on
/// locally (the prompt forbids generating one), the file is written under the persistent image
/// storage root, and the candidate's Url is the **relative storage key** — resolved back to a path
/// by whoever reads it, so the worker's install directory stays disposable.
/// </summary>
public sealed class CloudflareFluxImageGenerator(
    IHttpClientFactory httpClientFactory,
    CloudflareImagesOptions options,
    ImageStorage storage,
    ImageCompositor compositor,
    ILogger<CloudflareFluxImageGenerator> logger) : IAiImageGenerator
{
    /// <summary>Named HttpClient with a generation-friendly timeout and no resilience handler —
    /// retries are decided here, per error code, not blanket-applied.</summary>
    public const string HttpClientName = "cloudflare-ai";

    /// <summary>Caption per docs/05-integrations/images.md tier 3 — the site shows the
    /// attribution as the image credit, so readers see the cover is not a press photo.</summary>
    public const string IllustrationAttribution = "Илюстрация";

    /// <summary>Cloudflare accepts at most four reference images per FLUX.2 request.</summary>
    public const int MaxReferenceImages = 4;

    /// <summary>Cloudflare error code: the account's daily free Neurons allocation is used up.
    /// The only code that may arm the daily lock.</summary>
    public const int DailyAllocationExhaustedCode = 3036;

    /// <summary>Cloudflare error code: capacity temporarily exceeded. Transient — retry, never
    /// lock. Historically misread as an exhausted allocation because it also arrives as HTTP 429.</summary>
    public const int CapacityExceededCode = 3040;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PublicFigureDirectory figures = new(options.PublicFigures);

    public string Name => $"Cloudflare {ShortModelName(options.Model)}";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.AccountId) && !string.IsNullOrWhiteSpace(options.ApiToken);

    public async Task<AiImageResult> GenerateAsync(DraftContent content, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new CloudflareAiException("Cloudflare Workers AI is not configured (Images:Cloudflare:AccountId / ApiToken).");

        var reference = ResolvePersonReference(content);
        var prompt = FluxPromptComposer.Compose(
            content,
            reference is null ? null : new CoverPersonBrief(reference.Figure.Name, reference.Figure.Role),
            options.LogoCorner);

        var bytes = await RequestWithTransientRetryAsync(prompt, reference, ct).ConfigureAwait(false);
        bytes = ApplyLogo(bytes);

        var key = await SaveAsync(bytes, ct).ConfigureAwait(false);
        var image = new ImageCandidate(
            key,
            ThumbUrl: null,
            Name,
            IllustrationAttribution,
            options.Width,
            options.Height,
            ImageSourceKinds.Ai);
        // Neuron-priced inside the Workers AI free daily allocation, so 0 here, but the ledger
        // row still counts against Ai:Stages:Image:DailyRequestBudget and shows in /quota
        // (ADR-0010 metering).
        return new AiImageResult(image, new AiUsage("Cloudflare", options.Model, TokensIn: 0, TokensOut: 0, Cost: 0));
    }

    /// <summary>
    /// One generation attempt per pass, retrying only on <see cref="CapacityExceededCode"/>.
    /// Quota and ordinary failures propagate immediately — retrying a spend wall is exactly what
    /// must never happen, and retrying a rejected prompt only burns allocation.
    /// </summary>
    private async Task<byte[]> RequestWithTransientRetryAsync(
        string prompt, PersonReference? reference, CancellationToken ct)
    {
        var attempts = Math.Max(1, options.TransientRetries + 1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RequestAsync(prompt, reference, ct).ConfigureAwait(false);
            }
            catch (CloudflareAiException ex) when (ex.Transient && attempt < attempts)
            {
                var delay = TimeSpan.FromSeconds(options.TransientRetryDelaySeconds * attempt);
                logger.LogWarning(
                    "Cloudflare reported temporary capacity trouble (attempt {Attempt}/{Attempts}); retrying in {Delay}s: {Error}",
                    attempt, attempts, delay.TotalSeconds, ex.Message);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<byte[]> RequestAsync(string prompt, PersonReference? reference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.cloudflare.com/client/v4/accounts/{options.AccountId}/ai/run/{options.Model}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.ApiToken}");
        request.Content = BuildContent(prompt, reference);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, body);

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
        return bytes;
    }

    /// <summary>
    /// Classifies a failed response from the **structured** error code, not from words in the
    /// message. HTTP 429 alone means nothing: it carries both 3036 (allocation gone) and 3040
    /// (capacity blip). HTTP 402 is treated as a spend wall on its own, because a payment-required
    /// response must never be retried into a bill.
    /// </summary>
    private static CloudflareAiException Failure(HttpStatusCode statusCode, string body)
    {
        var codes = ErrorCodes(body);
        var detail = FirstErrorMessage(body);

        if (codes.Contains(DailyAllocationExhaustedCode) || statusCode == HttpStatusCode.PaymentRequired)
            return new CloudflareAiException(
                $"Cloudflare Workers AI free daily allocation exhausted (HTTP {(int)statusCode}): {detail}",
                quotaExhausted: true);

        if (codes.Contains(CapacityExceededCode) || statusCode == HttpStatusCode.ServiceUnavailable)
            return new CloudflareAiException(
                $"Cloudflare Workers AI capacity temporarily exceeded (HTTP {(int)statusCode}): {detail}",
                transient: true);

        return new CloudflareAiException(
            $"Cloudflare Workers AI returned HTTP {(int)statusCode}: {detail}");
    }

    /// <summary>
    /// FLUX.2 wants multipart/form-data even with nothing but a prompt; reference images ride
    /// along as <c>input_image_0..3</c>. <c>Steps</c>/<c>Guidance</c> are omitted at 0 because
    /// the distilled klein models reject a steps override outright.
    /// </summary>
    private HttpContent BuildContent(string prompt, PersonReference? reference)
    {
        if (options.RequestFormat == CloudflareRequestFormat.Json)
            return JsonContent.Create(
                new FluxRequest(prompt, options.Steps, options.Width, options.Height), options: JsonOptions);

        var form = new MultipartFormDataContent
        {
            { new StringContent(prompt), "prompt" },
            { new StringContent(Invariant(options.Width)), "width" },
            { new StringContent(Invariant(options.Height)), "height" },
        };
        if (options.Steps > 0)
            form.Add(new StringContent(Invariant(options.Steps)), "steps");
        if (options.Guidance > 0)
            form.Add(new StringContent(Invariant(options.Guidance)), "guidance");

        if (reference is not null)
        {
            var image = new ByteArrayContent(reference.Bytes);
            image.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
            form.Add(image, "input_image_0", reference.FileName);
        }
        return form;
    }

    /// <summary>Composites the configured logo asset onto the generated cover. A missing or
    /// unreadable logo is a warning, not a failure — the editor still gets the image.</summary>
    private byte[] ApplyLogo(byte[] coverBytes)
    {
        if (string.IsNullOrWhiteSpace(options.LogoFile))
            return coverBytes;

        if (!storage.TryResolve(options.LogoFile, out var logoPath) || !File.Exists(logoPath))
        {
            logger.LogWarning(
                "Cover logo {Logo} not found under the image storage root; publishing the cover without it",
                options.LogoFile);
            return coverBytes;
        }

        return compositor.OverlayLogo(
            coverBytes, File.ReadAllBytes(logoPath),
            options.LogoCorner, options.LogoWidthPercent, options.LogoMarginPercent);
    }

    /// <summary>
    /// Turns the drafting model's <see cref="DraftContent.ImagePersonName"/> into an actual
    /// reference photo, or nothing. Every gate has to pass: the JSON wire format cannot carry an
    /// image at all; the name must match a configured figure (a hallucinated one is dropped); the
    /// category must not be one where a face reads as an accusation; and the file must exist and be
    /// decodable — an oversized portrait is **resized**, not discarded. A skipped reference is a
    /// logged warning and an anonymous cover — never a likeness invented from the name.
    /// </summary>
    private PersonReference? ResolvePersonReference(DraftContent content)
    {
        if (string.IsNullOrWhiteSpace(content.ImagePersonName))
            return null;

        if (options.RequestFormat != CloudflareRequestFormat.Multipart)
        {
            logger.LogInformation(
                "Public figure {Person} skipped: {Model} takes no reference images, so the cover stays anonymous",
                content.ImagePersonName, options.Model);
            return null;
        }

        var figure = figures.Find(content.ImagePersonName);
        if (figure is null)
        {
            logger.LogWarning(
                "Drafting model returned public figure {Person}, who is not in Images:PublicFigures — ignored",
                content.ImagePersonName);
            return null;
        }

        if (!CoverPersonPolicy.MayDepict(content.Category, options.AllowPublicFiguresInSensitiveCategories))
        {
            logger.LogInformation(
                "Public figure {Person} not depicted: category {Category} is sensitive, using a symbolic cover",
                figure.Name, content.Category);
            return null;
        }

        var path = Path.Combine(storage.ReferenceDirectory, Path.GetFileName(figure.ReferenceImage));
        if (!File.Exists(path))
        {
            logger.LogWarning(
                "Reference photo for {Person} not found at {Path} — the cover stays anonymous",
                figure.Name, path);
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        if (!compositor.ShrinkToReferenceLimit(bytes, out var sized))
        {
            logger.LogWarning(
                "Reference photo for {Person} at {Path} is not a decodable image — the cover stays anonymous",
                figure.Name, path);
            return null;
        }
        if (!ReferenceEquals(sized, bytes))
            logger.LogInformation(
                "Reference photo for {Person} downscaled to fit Cloudflare's {Max}px reference limit",
                figure.Name, ImageDimensions.MaxReferenceSide);

        var fileName = Path.GetFileName(path);
        // A resized reference is always re-encoded as JPEG by the compositor.
        var contentType = ReferenceEquals(sized, bytes)
            && fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
        return new PersonReference(figure, sized, fileName, contentType);
    }

    /// <summary>The REST envelope is {"result":{"image":"&lt;base64 JPEG&gt;"},"success":true,...};
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
            throw Failure(HttpStatusCode.OK, body);
        if (string.IsNullOrWhiteSpace(envelope.Result?.Image))
            throw new CloudflareAiException("Cloudflare Workers AI returned no image.");
        return envelope.Result.Image;
    }

    /// <summary>Writes the cover under the generated-images area and returns its relative storage
    /// key — what goes into nw_DraftImage.Url.</summary>
    private async Task<string> SaveAsync(byte[] bytes, CancellationToken ct)
    {
        var dir = storage.EnsureDirectory(storage.GeneratedArea);
        var fileName = $"flux-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.jpg";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes, ct).ConfigureAwait(false);
        return storage.GeneratedKey(fileName);
    }

    /// <summary>Every <c>code</c> in the envelope's errors array — the classification input.</summary>
    private static IReadOnlyCollection<int> ErrorCodes(string body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<FluxEnvelope>(body, JsonOptions);
            return envelope?.Errors?.Select(e => e.Code).ToHashSet() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The entries of the envelope's errors array ("code: message"), falling back to the
    /// raw body — this text ends up in the fallback warning log and the Telegram alert.</summary>
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

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"@cf/black-forest-labs/flux-2-klein-4b" → "flux-2-klein-4b" — what the provider
    /// name in logs and the image credit shows.</summary>
    private static string ShortModelName(string model) =>
        model.Split('/').LastOrDefault() is { Length: > 0 } tail ? tail : model;

    /// <summary>An approved reference photo, already read and sized for Cloudflare.</summary>
    private sealed record PersonReference(
        PublicFigure Figure, byte[] Bytes, string FileName, string ContentType);

    /// <summary>Wire shape of the legacy JSON generation request (FLUX.1 Schnell; width/height
    /// are honoured even though the public schema only documents prompt/steps/seed).</summary>
    private sealed record FluxRequest(string Prompt, int Steps, int Width, int Height);

    /// <summary>Wire shape of the REST envelope (fields we use only).</summary>
    private sealed record FluxEnvelope(FluxResult? Result, bool Success, List<FluxError>? Errors);

    private sealed record FluxResult(string? Image);

    private sealed record FluxError(int Code, string? Message);
}
