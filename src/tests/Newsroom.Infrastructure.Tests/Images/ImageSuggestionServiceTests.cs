using Microsoft.Extensions.Logging.Abstractions;
using Newsroom.Core.Drafting;
using Newsroom.Infrastructure.Images;

namespace Newsroom.Infrastructure.Tests.Images;

public class ImageSuggestionServiceTests
{
    private static ImageCandidate Candidate(string url, string provider = "Fake") =>
        new(url, url + "?thumb", provider, $"{provider} / tester", 800, 600);

    private static ImageSuggestionService CreateService(
        IEnumerable<IImageProvider> providers, int maxSuggestions = 3) =>
        new(providers, new ImagesOptions { MaxSuggestions = maxSuggestions },
            NullLogger<ImageSuggestionService>.Instance);

    [Fact]
    public async Task Collects_candidates_across_queries_up_to_the_cap()
    {
        var provider = new FakeImageProvider("A", query => [Candidate($"https://a.example/{query}")]);
        var service = CreateService([provider], maxSuggestions: 3);

        var result = await service.SuggestAsync(["one", "two", "three", "four"], CancellationToken.None);

        Assert.Equal(3, result.Count); // capped at MaxSuggestions
        Assert.Equal(3, provider.Calls.Count);
        Assert.Equal(["one", "two", "three"], provider.Calls);
    }

    [Fact]
    public async Task Unconfigured_providers_are_skipped()
    {
        var unconfigured = new FakeImageProvider("Off",
            _ => throw new InvalidOperationException("must not be called"))
        { IsConfigured = false };
        var configured = new FakeImageProvider("On", _ => [Candidate("https://on.example/1")]);
        var service = CreateService([unconfigured, configured]);

        var result = await service.SuggestAsync(["query"], CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal("https://on.example/1", candidate.Url);
        Assert.Empty(unconfigured.Calls);
    }

    [Fact]
    public async Task No_configured_providers_yield_an_empty_list()
    {
        var unconfigured = new FakeImageProvider("Off", _ => [Candidate("https://x.example/1")])
        { IsConfigured = false };
        var service = CreateService([unconfigured]);

        Assert.Empty(await service.SuggestAsync(["query"], CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_urls_are_deduplicated_case_insensitively()
    {
        var provider = new FakeImageProvider("A", _ =>
            [Candidate("https://a.example/same"), Candidate("HTTPS://A.EXAMPLE/SAME")]);
        var service = CreateService([provider]);

        var result = await service.SuggestAsync(["one", "two"], CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal("https://a.example/same", candidate.Url);
    }

    [Fact]
    public async Task A_failing_provider_is_tolerated_and_the_other_still_delivers()
    {
        var broken = new FakeImageProvider("Broken",
            _ => throw new HttpRequestException("api down"));
        var working = new FakeImageProvider("Working", query =>
            [Candidate($"https://working.example/{query}", "Working")]);
        var service = CreateService([broken, working]);

        var result = await service.SuggestAsync(["one", "two"], CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, c => Assert.Equal("Working", c.ProviderName));
    }

    [Fact]
    public async Task Queries_are_spread_round_robin_over_the_configured_providers()
    {
        var first = new FakeImageProvider("First", query => [Candidate($"https://first.example/{query}", "First")]);
        var second = new FakeImageProvider("Second", query => [Candidate($"https://second.example/{query}", "Second")]);
        var service = CreateService([first, second], maxSuggestions: 2);

        var result = await service.SuggestAsync(["one", "two"], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(["one"], first.Calls);   // query 1 → provider 1
        Assert.Equal(["two"], second.Calls);  // query 2 → provider 2
    }

    [Fact]
    public async Task Empty_query_list_yields_an_empty_list()
    {
        var provider = new FakeImageProvider("A", _ => [Candidate("https://a.example/1")]);
        var service = CreateService([provider]);

        Assert.Empty(await service.SuggestAsync([], CancellationToken.None));
        Assert.Empty(provider.Calls);
    }

    private sealed class FakeImageProvider(
        string name, Func<string, IReadOnlyList<ImageCandidate>> respond) : IImageProvider
    {
        public List<string> Calls { get; } = [];

        public string Name => name;

        public bool IsConfigured { get; init; } = true;

        public Task<IReadOnlyList<ImageCandidate>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Calls.Add(query);
            return Task.FromResult(respond(query));
        }
    }
}
