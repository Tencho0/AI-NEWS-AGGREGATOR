using Microsoft.Extensions.Logging.Abstractions;

using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Infrastructure.Images;

namespace Newsroom.Infrastructure.Tests.Images;

public class FeaturedImageServiceTests
{
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

    private static AiImageResult GeneratedResult() => new(
        new ImageCandidate(
            @"C:\worker\generated-images\flux-1.jpg", null, "FakeGen", "Илюстрация",
            1280, 720, ImageSourceKinds.Ai),
        new AiUsage("Cloudflare", "flux-1-schnell", 0, 0, 0));

    private static (FeaturedImageService Service, FakeImageProvider Stock, FakeAiBudget Budget) CreateService(
        FakeAiImageGenerator generator)
    {
        var stock = new FakeImageProvider();
        var suggestions = new ImageSuggestionService(
            [stock], new ImagesOptions { MaxSuggestions = 3 },
            NullLogger<ImageSuggestionService>.Instance);
        var budget = new FakeAiBudget();
        var service = new FeaturedImageService(
            generator, suggestions, budget, NullLogger<FeaturedImageService>.Instance);
        return (service, stock, budget);
    }

    [Fact]
    public async Task A_successful_generation_is_the_single_candidate_and_stock_is_skipped()
    {
        var generator = new FakeAiImageGenerator { OnGenerate = _ => GeneratedResult() };
        var (service, stock, budget) = CreateService(generator);

        var candidates = await service.GetCandidatesAsync(Draft(), CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(ImageSourceKinds.Ai, candidate.SourceKind);
        Assert.Empty(stock.Queries);

        var (stage, usage) = Assert.Single(budget.Recorded);
        Assert.Equal(FeaturedImageService.Stage, stage);
        Assert.Equal("Cloudflare", usage.Provider);
    }

    [Fact]
    public async Task A_failing_generator_falls_back_to_the_stock_providers()
    {
        var generator = new FakeAiImageGenerator
        {
            OnGenerate = _ => throw new CloudflareAiException("quota exhausted"),
        };
        var (service, stock, budget) = CreateService(generator);

        var candidates = await service.GetCandidatesAsync(Draft(), CancellationToken.None);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Equal(ImageSourceKinds.Stock, c.SourceKind));
        Assert.Equal(["city hall bulgaria"], stock.Queries);
        Assert.Empty(budget.Recorded); // nothing landed, nothing metered
    }

    [Fact]
    public async Task An_unconfigured_generator_goes_straight_to_stock_without_touching_the_budget()
    {
        var generator = new FakeAiImageGenerator { IsConfigured = false };
        var (service, stock, budget) = CreateService(generator);

        var candidates = await service.GetCandidatesAsync(Draft(), CancellationToken.None);

        Assert.NotEmpty(candidates);
        Assert.Equal(0, generator.Calls);
        Assert.Equal(["city hall bulgaria"], stock.Queries);
        Assert.Equal(0, budget.ReserveCalls);
    }

    [Fact]
    public async Task An_exhausted_image_budget_skips_generation_and_uses_stock()
    {
        var generator = new FakeAiImageGenerator { OnGenerate = _ => GeneratedResult() };
        var (service, stock, budget) = CreateService(generator);
        budget.HasBudget = false;

        var candidates = await service.GetCandidatesAsync(Draft(), CancellationToken.None);

        Assert.NotEmpty(candidates);
        Assert.Equal(0, generator.Calls);
        Assert.Equal(["city hall bulgaria"], stock.Queries);
    }

    private sealed class FakeAiImageGenerator : IAiImageGenerator
    {
        public Func<DraftContent, AiImageResult>? OnGenerate { get; init; }

        public bool IsConfigured { get; init; } = true;

        public int Calls { get; private set; }

        public string Name => "FakeGen";

        public Task<AiImageResult> GenerateAsync(DraftContent content, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(OnGenerate!(content));
        }
    }

    private sealed class FakeAiBudget : IAiBudget
    {
        public bool HasBudget { get; set; } = true;

        public int ReserveCalls { get; private set; }

        public List<(string Stage, AiUsage Usage)> Recorded { get; } = [];

        public Task<bool> TryReserveAsync(string stage, CancellationToken ct)
        {
            ReserveCalls++;
            return Task.FromResult(HasBudget);
        }

        public Task RecordAsync(string stage, AiUsage usage, CancellationToken ct)
        {
            Recorded.Add((stage, usage));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeImageProvider : IImageProvider
    {
        public List<string> Queries { get; } = [];

        public string Name => "FakeStock";

        public bool IsConfigured => true;

        public Task<IReadOnlyList<ImageCandidate>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<ImageCandidate>>(
                [new ImageCandidate($"https://stock.example/{Queries.Count}.jpg", null, Name, "FakeStock / tester", 1600, 900)]);
        }
    }
}
