using System.Net;
using System.Text.Json;

using Newsroom.Core.Drafting;
using Newsroom.Infrastructure.Images;

namespace Newsroom.Infrastructure.Tests.Images;

public class CloudflareFluxImageGeneratorTests
{
    private static readonly byte[] FakeJpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03];

    private static readonly string SuccessJson =
        $$"""{"result":{"image":"{{Convert.ToBase64String(FakeJpegBytes)}}"},"success":true,"errors":[],"messages":[]}""";

    private static DraftContent Draft() => new(
        Headline: "НОВИ МЕРКИ В БЛАГОЕВГРАД",
        Subtitle: null,
        BodyMarkdown: "Тяло.",
        Category: "Общество",
        Region: "Благоевград",
        Tags: [],
        SeoTitle: "Нови мерки",
        SeoDescription: "Описание.",
        ImageSearchQueries: ["city hall bulgaria"],
        ImageAltTextBg: "Сградата на общината",
        FlaggedClaims: [],
        Confidence: 0.8,
        FacebookCaption: "Кратък текст",
        FacebookHashtags: []);

    private static (CloudflareFluxImageGenerator Generator, CannedResponseHandler Handler, string Dir) CreateGenerator(
        string json = "", HttpStatusCode statusCode = HttpStatusCode.OK, string apiToken = "cf-token")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nw-flux-{Guid.NewGuid():N}");
        var handler = new CannedResponseHandler(json.Length > 0 ? json : SuccessJson, statusCode);
        var generator = new CloudflareFluxImageGenerator(
            new FakeHttpClientFactory(handler),
            new CloudflareImagesOptions
            {
                AccountId = "acc-123",
                ApiToken = apiToken,
                Steps = 6,
                Width = 1280,
                Height = 720,
                GeneratedImageDir = dir,
            });
        return (generator, handler, dir);
    }

    [Fact]
    public async Task Generates_saves_the_jpeg_and_returns_an_ai_candidate()
    {
        var (generator, _, dir) = CreateGenerator();
        try
        {
            var result = await generator.GenerateAsync(Draft(), CancellationToken.None);

            var image = result.Image;
            Assert.Equal(ImageSourceKinds.Ai, image.SourceKind);
            Assert.Equal("Cloudflare FLUX.1 Schnell", image.ProviderName);
            Assert.Equal(CloudflareFluxImageGenerator.IllustrationAttribution, image.Attribution);
            Assert.Equal(1280, image.Width);
            Assert.Equal(720, image.Height);
            Assert.Null(image.ThumbUrl);

            Assert.True(File.Exists(image.Url)); // Url is the worker-local path
            Assert.Equal(FakeJpegBytes, await File.ReadAllBytesAsync(image.Url));
            Assert.EndsWith(".jpg", image.Url);

            Assert.Equal("Cloudflare", result.Usage.Provider);
            Assert.Equal("@cf/black-forest-labs/flux-1-schnell", result.Usage.Model);
            Assert.Equal(0, result.Usage.Cost);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Request_carries_the_bearer_token_model_route_and_generation_parameters()
    {
        var (generator, handler, dir) = CreateGenerator();
        try
        {
            await generator.GenerateAsync(Draft(), CancellationToken.None);

            var request = handler.LastRequest!;
            Assert.Equal(
                "https://api.cloudflare.com/client/v4/accounts/acc-123/ai/run/@cf/black-forest-labs/flux-1-schnell",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer cf-token", request.Headers.GetValues("Authorization").Single());

            using var body = JsonDocument.Parse(handler.LastRequestBody!);
            var root = body.RootElement;
            Assert.Contains("city hall bulgaria", root.GetProperty("prompt").GetString());
            Assert.Equal(6, root.GetProperty("steps").GetInt32());
            Assert.Equal(1280, root.GetProperty("width").GetInt32());
            Assert.Equal(720, root.GetProperty("height").GetInt32());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

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
    public async Task Quota_exhaustion_throws_with_the_provider_error_text()
    {
        var (generator, _, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":3040,"message":"Capacity temporarily exceeded"}]}""",
            HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

        Assert.Contains("429", ex.Message);
        Assert.Contains("3040: Capacity temporarily exceeded", ex.Message);
    }

    [Fact]
    public async Task An_http_error_throws_with_the_status_code()
    {
        var (generator, _, _) = CreateGenerator(
            """{"result":null,"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}""",
            HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<CloudflareAiException>(
            () => generator.GenerateAsync(Draft(), CancellationToken.None));

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
}
