using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Newsroom.Core.Drafting;
using Newsroom.Core.Images;
using Newsroom.Infrastructure.Images;

using SkiaSharp;

namespace Newsroom.Infrastructure.Tests.Images;

public class CloudflareFluxImageGeneratorTests : IDisposable
{
    private static readonly byte[] FakeJpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03];

    private static readonly string SuccessJson =
        $$"""{"result":{"image":"{{Convert.ToBase64String(FakeJpegBytes)}}"},"success":true,"errors":[],"messages":[]}""";

    private static readonly PublicFigure Mayor =
        new("Иван Иванов", "кмет на Благоевград", "ivanov.png", ["кметът Иванов"]);

    private readonly List<string> tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in tempDirs.Where(Directory.Exists))
            Directory.Delete(dir, recursive: true);
    }

    private static DraftContent Draft(
        string? person = null, string category = "Общество", CoverTextPlan? coverText = null) => new(
        Headline: "НОВИ МЕРКИ В БЛАГОЕВГРАД",
        Subtitle: null,
        BodyMarkdown: "Тяло.",
        Category: category,
        Region: "Благоевград",
        Tags: [],
        SeoTitle: "Нови мерки",
        SeoDescription: "Описание.",
        ImageSearchQueries: ["city hall bulgaria"],
        ImageAltTextBg: "Сградата на общината",
        FlaggedClaims: [],
        Confidence: 0.8,
        FacebookCaption: "Кратък текст",
        FacebookHashtags: [],
        ImageScene: "a mayor signing a contract at a municipal desk on a bright morning",
        ImagePersonName: person,
        CoverText: coverText);

    private (CloudflareFluxImageGenerator Generator, CannedResponseHandler Handler, ImageStorage Storage) CreateGenerator(
        string json = "",
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string apiToken = "cf-token",
        CloudflareRequestFormat format = CloudflareRequestFormat.Multipart,
        int steps = 0,
        IReadOnlyList<PublicFigure>? figures = null,
        bool allowInSensitive = false,
        string logoFile = "",
        int transientRetries = 2,
        bool burnInCoverText = true)
    {
        var storage = NewStorage();
        var handler = new CannedResponseHandler(json.Length > 0 ? json : SuccessJson, statusCode);
        var generator = new CloudflareFluxImageGenerator(
            new FakeHttpClientFactory(handler),
            new CloudflareImagesOptions
            {
                AccountId = "acc-123",
                ApiToken = apiToken,
                RequestFormat = format,
                Steps = steps,
                Width = 1280,
                Height = 720,
                TransientRetries = transientRetries,
                TransientRetryDelaySeconds = 0, // no real waiting in tests
                LogoFile = logoFile,
                PublicFigures = figures ?? [],
                AllowPublicFiguresInSensitiveCategories = allowInSensitive,
                BurnInCoverText = burnInCoverText,
            },
            storage,
            new ImageCompositor(NullLogger<ImageCompositor>.Instance),
            NullLogger<CloudflareFluxImageGenerator>.Instance);
        return (generator, handler, storage);
    }

    private ImageStorage NewStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nw-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        tempDirs.Add(root);
        return new ImageStorage(root);
    }

    /// <summary>Writes a real, decodable reference portrait of the given size.</summary>
    private static void WriteReference(ImageStorage storage, string fileName, int width, int height)
    {
        var dir = storage.EnsureDirectory(storage.ReferenceArea);
        File.WriteAllBytes(Path.Combine(dir, fileName), RealImage(width, height, SKEncodedImageFormat.Png));
    }

    private static byte[] RealImage(int width, int height, SKEncodedImageFormat format)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(40, 80, 160));
        surface.Canvas.Flush();
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    [Fact]
    public async Task Generates_saves_the_jpeg_and_returns_a_relative_storage_key()
    {
        var (generator, _, storage) = CreateGenerator();

        var result = await generator.GenerateAsync(Draft(), CancellationToken.None);

        var image = result.Image;
        Assert.Equal(ImageSourceKinds.Ai, image.SourceKind);
        Assert.Equal("Cloudflare flux-2-klein-4b", image.ProviderName);
        Assert.Equal(CloudflareFluxImageGenerator.IllustrationAttribution, image.Attribution);
        Assert.Equal(1280, image.Width);
        Assert.Equal(720, image.Height);
        Assert.Null(image.ThumbUrl);

        // Url is a relative key, never an absolute path (ADR-0013).
        Assert.StartsWith("generated-images/", image.Url);
        Assert.EndsWith(".jpg", image.Url);
        Assert.False(Path.IsPathRooted(image.Url));

        Assert.True(storage.TryResolve(image.Url, out var path));
        Assert.True(File.Exists(path));
        Assert.Equal(FakeJpegBytes, await File.ReadAllBytesAsync(path));
        Assert.StartsWith(storage.GeneratedDirectory, path);

        Assert.Equal("Cloudflare", result.Usage.Provider);
        Assert.Equal(CloudflareImagesOptions.DefaultModel, result.Usage.Model);
        Assert.Equal(0, result.Usage.Cost);
    }

    [Fact]
    public async Task Posts_multipart_form_data_with_the_prompt_and_frame_size()
    {
        var (generator, handler, _) = CreateGenerator();

        await generator.GenerateAsync(Draft(), CancellationToken.None);

        var request = handler.LastRequest!;
        Assert.Equal(
            "https://api.cloudflare.com/client/v4/accounts/acc-123/ai/run/@cf/black-forest-labs/flux-2-klein-4b",
            request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer cf-token", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);

        // HttpClient writes form-field names unquoted (name=prompt); only filenames get quotes.
        var body = handler.LastRequestBody!;
        Assert.Matches(FormField("prompt"), body);
        Assert.Contains("a mayor signing a contract at a municipal desk", body);
        Assert.Matches(FormField("width"), body);
        Assert.Contains("1280", body);
        Assert.Matches(FormField("height"), body);
        Assert.Contains("720", body);
    }

    [Fact]
    public async Task Cover_text_reaches_the_model_as_quoted_strings()
    {
        var (generator, handler, _) = CreateGenerator();

        await generator.GenerateAsync(
            Draft(coverText: new CoverTextPlan("ПОЖАР В ПЕТРИЧ", ["3 сгради"])), CancellationToken.None);

        var body = handler.LastRequestBody!;
        Assert.Contains("\"ПОЖАР В ПЕТРИЧ\"", body);
        Assert.Contains("\"3 сгради\"", body);
        Assert.Contains("no logo, wordmark, brand name or watermark", body);
    }

    /// <summary>Images:Cover:BurnInText=false — FLUX.2 klein cannot spell Cyrillic (live 2026-07-31:
    /// "Обновени сгради" came back as "Обоввейк сргади"), so no glyphs are requested at all until the
    /// local renderer lands. The headline may still travel as context the model must not draw.</summary>
    [Fact]
    public async Task Cover_text_is_withheld_from_the_model_when_burn_in_is_switched_off()
    {
        var (generator, handler, _) = CreateGenerator(burnInCoverText: false);

        await generator.GenerateAsync(
            Draft(coverText: new CoverTextPlan("ПОЖАР В ПЕТРИЧ", ["3 сгради"])), CancellationToken.None);

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("\"ПОЖАР В ПЕТРИЧ\"", body);
        Assert.DoesNotContain("\"3 сгради\"", body);
        Assert.DoesNotContain("Render text into the image", body);
        Assert.Contains("never render it as text", body);
    }

    [Fact]
    public async Task Steps_are_omitted_by_default_because_klein_fixes_them()
    {
        var (generator, handler, _) = CreateGenerator();

        await generator.GenerateAsync(Draft(), CancellationToken.None);

        Assert.DoesNotMatch(FormField("steps"), handler.LastRequestBody!);
    }

    [Fact]
    public async Task A_configured_step_count_is_sent_for_models_that_accept_one()
    {
        var (generator, handler, _) = CreateGenerator(steps: 25);

        await generator.GenerateAsync(Draft(), CancellationToken.None);

        Assert.Matches(FormField("steps"), handler.LastRequestBody!);
        Assert.Contains("25", handler.LastRequestBody!);
    }

    [Fact]
    public async Task The_legacy_json_format_still_posts_prompt_steps_and_size()
    {
        var (generator, handler, _) = CreateGenerator(format: CloudflareRequestFormat.Json, steps: 6);

        await generator.GenerateAsync(Draft(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Contains("a mayor signing a contract", root.GetProperty("prompt").GetString());
        Assert.Equal(6, root.GetProperty("steps").GetInt32());
        Assert.Equal(1280, root.GetProperty("width").GetInt32());
        Assert.Equal(720, root.GetProperty("height").GetInt32());
    }

    // ---- Public-figure reference photos -------------------------------------------------------

    [Fact]
    public async Task An_approved_reference_photo_is_attached_and_named_in_the_prompt()
    {
        var (generator, handler, storage) = CreateGenerator(figures: [Mayor]);
        WriteReference(storage, "ivanov.png", 400, 400);

        await generator.GenerateAsync(Draft(person: "Иван Иванов"), CancellationToken.None);

        var body = handler.LastRequestBody!;
        Assert.Matches(FormField("input_image_0"), body);
        Assert.Contains("ivanov.png", body); // the reference file rides along as the part's filename
        Assert.Contains("the person in reference image 1 is Иван Иванов, кмет на Благоевград", body);
    }

    [Fact]
    public async Task An_oversized_reference_photo_is_resized_rather_than_dropped()
    {
        var (generator, handler, storage) = CreateGenerator(figures: [Mayor]);
        WriteReference(storage, "ivanov.png", 1200, 900);

        await generator.GenerateAsync(Draft(person: "Иван Иванов"), CancellationToken.None);

        // The likeness survives — the photo is downscaled to fit Cloudflare's 512px limit.
        Assert.Matches(FormField("input_image_0"), handler.LastRequestBody!);
        Assert.Contains("the person in reference image 1 is Иван Иванов", handler.LastRequestBody!);
    }

    [Fact]
    public void Resizing_preserves_aspect_ratio_and_never_upscales()
    {
        var compositor = new ImageCompositor(NullLogger<ImageCompositor>.Instance);

        Assert.True(compositor.ShrinkToReferenceLimit(
            RealImage(1200, 900, SKEncodedImageFormat.Png), out var shrunk));
        Assert.True(ImageDimensions.TryRead(shrunk, out var width, out var height));
        Assert.True(width < ImageDimensions.MaxReferenceSide, $"width {width} must be under 512");
        Assert.True(height < ImageDimensions.MaxReferenceSide, $"height {height} must be under 512");
        Assert.InRange(width, 505, 511);                    // long side pinned close to the limit
        Assert.InRange(width / (double)height, 1.32, 1.35); // 4:3 preserved

        var small = RealImage(200, 150, SKEncodedImageFormat.Png);
        Assert.True(compositor.ShrinkToReferenceLimit(small, out var untouched));
        Assert.Same(small, untouched);                  // already small: no re-encode, no upscale
    }

    [Fact]
    public async Task A_name_that_is_not_a_configured_figure_never_becomes_a_likeness()
    {
        var (generator, handler, storage) = CreateGenerator(figures: [Mayor]);
        WriteReference(storage, "ivanov.png", 400, 400);

        await generator.GenerateAsync(Draft(person: "Георги Георгиев"), CancellationToken.None);

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("input_image_0", body);
        Assert.DoesNotContain("Георги Георгиев", body);
        Assert.Contains("Depict no real public figure", body);
    }

    [Fact]
    public async Task A_missing_reference_file_leaves_the_cover_anonymous()
    {
        var (generator, handler, _) = CreateGenerator(figures: [Mayor]);

        await generator.GenerateAsync(Draft(person: "Иван Иванов"), CancellationToken.None);

        Assert.DoesNotContain("input_image_0", handler.LastRequestBody!);
        Assert.Contains("faces non-identifiable", handler.LastRequestBody!);
    }

    [Fact]
    public async Task An_undecodable_reference_file_leaves_the_cover_anonymous()
    {
        var (generator, handler, storage) = CreateGenerator(figures: [Mayor]);
        var dir = storage.EnsureDirectory(storage.ReferenceArea);
        File.WriteAllBytes(Path.Combine(dir, "ivanov.png"), [1, 2, 3, 4, 5]);

        await generator.GenerateAsync(Draft(person: "Иван Иванов"), CancellationToken.None);

        Assert.DoesNotContain("input_image_0", handler.LastRequestBody!);
    }

    [Fact]
    public async Task Crime_stories_get_a_symbolic_cover_instead_of_the_figures_face()
    {
        var (generator, handler, storage) = CreateGenerator(figures: [Mayor]);
        WriteReference(storage, "ivanov.png", 400, 400);

        await generator.GenerateAsync(
            Draft(person: "Иван Иванов", category: "Криминално"), CancellationToken.None);

        Assert.DoesNotContain("input_image_0", handler.LastRequestBody!);
        Assert.Contains("Depict no real public figure", handler.LastRequestBody!);
    }

    [Fact]
    public async Task The_sensitive_category_override_lets_a_central_figure_through()
    {
        var (generator, handler, storage) = CreateGenerator(figures: [Mayor], allowInSensitive: true);
        WriteReference(storage, "ivanov.png", 400, 400);

        await generator.GenerateAsync(
            Draft(person: "Иван Иванов", category: "Криминално"), CancellationToken.None);

        Assert.Matches(FormField("input_image_0"), handler.LastRequestBody!);
        Assert.Contains("imply no guilt, arrest, detention", handler.LastRequestBody!);
    }

    [Fact]
    public async Task The_json_format_carries_no_reference_image_so_the_cover_stays_anonymous()
    {
        var (generator, handler, storage) = CreateGenerator(
            format: CloudflareRequestFormat.Json, figures: [Mayor]);
        WriteReference(storage, "ivanov.png", 400, 400);

        await generator.GenerateAsync(Draft(person: "Иван Иванов"), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Contains("Depict no real public figure", body.RootElement.GetProperty("prompt").GetString());
    }

    // ---- Logo overlay ------------------------------------------------------------------------

    [Fact]
    public async Task The_real_logo_is_composited_onto_the_generated_cover()
    {
        var (_, _, storage) = CreateGenerator(logoFile: "branding/logo.png");
        var branding = storage.EnsureDirectory("branding");
        File.WriteAllBytes(Path.Combine(branding, "logo.png"), RealImage(200, 60, SKEncodedImageFormat.Png));
        // A real cover, so the compositor has something decodable to draw onto.
        var cover = RealImage(1280, 720, SKEncodedImageFormat.Jpeg);
        var (withCover, _, coverStorage) = CreateGeneratorWithImage(cover, storage);

        var result = await withCover.GenerateAsync(Draft(), CancellationToken.None);

        Assert.True(coverStorage.TryResolve(result.Image.Url, out var path));
        var saved = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(cover, saved);                  // re-encoded with the logo drawn on
        Assert.True(ImageDimensions.TryRead(saved, out var width, out var height));
        Assert.Equal(1280, width);                      // overlay never changes the frame size
        Assert.Equal(720, height);
    }

    [Fact]
    public async Task A_missing_logo_asset_does_not_cost_the_cover()
    {
        var (generator, _, storage) = CreateGenerator(logoFile: "branding/absent.png");

        var result = await generator.GenerateAsync(Draft(), CancellationToken.None);

        Assert.True(storage.TryResolve(result.Image.Url, out var path));
        Assert.Equal(FakeJpegBytes, await File.ReadAllBytesAsync(path)); // untouched
    }

    [Fact]
    public async Task A_logo_path_outside_the_storage_root_is_refused()
    {
        var (generator, _, storage) = CreateGenerator(logoFile: "../../../etc/passwd");

        var result = await generator.GenerateAsync(Draft(), CancellationToken.None);

        Assert.True(storage.TryResolve(result.Image.Url, out var path));
        Assert.Equal(FakeJpegBytes, await File.ReadAllBytesAsync(path));
    }

    // ---- Failure classification --------------------------------------------------------------

    [Fact]
    public async Task Unconfigured_generator_reports_itself_and_never_calls_the_api()
    {
        var (generator, handler, _) = CreateGenerator(apiToken: "");

        Assert.False(generator.IsConfigured);
        await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Error_3036_on_a_429_is_the_exhausted_daily_allocation()
    {
        var (generator, _, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":3036,"message":"Account limited"}]}""",
            HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.True(ex.QuotaExhausted);
        Assert.False(ex.Transient);
        Assert.Contains("3036: Account limited", ex.Message);
    }

    [Fact]
    public async Task Error_3040_on_the_same_429_is_transient_and_never_arms_the_daily_lock()
    {
        // The bug this replaces: every 429 was read as an exhausted allocation.
        var (generator, handler, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":3040,"message":"Capacity temporarily exceeded"}]}""",
            HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.False(ex.QuotaExhausted);
        Assert.True(ex.Transient);
        Assert.Equal(3, handler.RequestCount); // 1 attempt + TransientRetries(2)
    }

    [Fact]
    public async Task Transient_retries_are_bounded_by_configuration()
    {
        var (generator, handler, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":3040,"message":"Capacity"}]}""",
            HttpStatusCode.TooManyRequests,
            transientRetries: 0);

        await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Http_402_is_a_spend_wall_regardless_of_the_error_body()
    {
        var (generator, handler, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":10001,"message":"Payment required"}]}""",
            HttpStatusCode.PaymentRequired);

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.True(ex.QuotaExhausted);
        Assert.Equal(1, handler.RequestCount); // never retried into a bill
    }

    [Theory]
    [InlineData("You have exceeded your daily Neurons allocation")]
    [InlineData("Enable billing to continue using this model")]
    [InlineData("This model requires a paid plan")]
    public async Task Quota_sounding_words_alone_do_not_arm_the_daily_lock(string message)
    {
        // Classification is by structured error code, never by keywords in the message.
        var (generator, _, _) = CreateGenerator(
            $$"""{"result":null,"success":false,"errors":[{"code":1000,"message":"{{message}}"}]}""",
            HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.False(ex.QuotaExhausted);
        Assert.False(ex.Transient);
    }

    [Fact]
    public async Task An_ordinary_http_error_is_neither_quota_nor_transient()
    {
        var (generator, _, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}""",
            HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.False(ex.QuotaExhausted);
        Assert.False(ex.Transient);
        Assert.Contains("401", ex.Message);
        Assert.Contains("Authentication error", ex.Message);
    }

    [Fact]
    public async Task A_success_false_envelope_on_http_200_throws()
    {
        var (generator, _, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":5006,"message":"prompt too long"}]}""");

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.Contains("5006: prompt too long", ex.Message);
        Assert.False(ex.QuotaExhausted);
    }

    [Fact]
    public async Task A_missing_image_field_throws()
    {
        var (generator, _, _) = CreateGenerator("""{"result":{},"success":true,"errors":[]}""");

        await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_base64_throws_instead_of_saving_garbage()
    {
        var (generator, _, _) = CreateGenerator(
            """{"result":{"image":"not-base-64!!!"},"success":true,"errors":[]}""");

        await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));
    }

    /// <summary>A generator whose canned response returns <paramref name="image"/>, sharing
    /// <paramref name="storage"/> so assets written by the caller are visible to it.</summary>
    private (CloudflareFluxImageGenerator Generator, CannedResponseHandler Handler, ImageStorage Storage) CreateGeneratorWithImage(
        byte[] image, ImageStorage storage)
    {
        var json = $$"""{"result":{"image":"{{Convert.ToBase64String(image)}}"},"success":true,"errors":[]}""";
        var handler = new CannedResponseHandler(json);
        var generator = new CloudflareFluxImageGenerator(
            new FakeHttpClientFactory(handler),
            new CloudflareImagesOptions
            {
                AccountId = "acc-123",
                ApiToken = "cf-token",
                Width = 1280,
                Height = 720,
                TransientRetryDelaySeconds = 0,
                LogoFile = "branding/logo.png",
            },
            storage,
            new ImageCompositor(NullLogger<ImageCompositor>.Instance),
            NullLogger<CloudflareFluxImageGenerator>.Instance);
        return (generator, handler, storage);
    }

    /// <summary>Matches one multipart form field by name, with or without quotes around it —
    /// HttpClient emits <c>name=prompt</c>, but the quoted form is equally valid.</summary>
    private static string FormField(string name) => $"name=\"?{name}\"?";
}
