using Newsroom.Infrastructure.Images;

namespace Newsroom.Infrastructure.Tests.Images;

public class PexelsImageProviderTests
{
    private const string CannedJson =
        """
        {
          "page": 1,
          "photos": [
            {"id": 10, "width": 4000, "height": 3000, "photographer": "John Doe",
             "src": {"large": "https://images.pexels.com/large1.jpg",
                     "medium": "https://images.pexels.com/medium1.jpg"}},
            {"id": 11, "photographer": "Jane",
             "src": {"medium": "https://images.pexels.com/medium2.jpg"}},
            {"id": 12, "photographer": "Empty", "src": {}}
          ]
        }
        """;

    private static (PexelsImageProvider Provider, CannedResponseHandler Handler) CreateProvider(
        string json = CannedJson, string apiKey = "pex-key")
    {
        var handler = new CannedResponseHandler(json);
        var provider = new PexelsImageProvider(
            new FakeHttpClientFactory(handler), new ImagesOptions { PexelsApiKey = apiKey });
        return (provider, handler);
    }

    [Fact]
    public async Task Parses_photos_with_url_fallbacks_and_attribution()
    {
        var (provider, _) = CreateProvider();

        var candidates = await provider.SearchAsync("mountain village", 3, CancellationToken.None);

        Assert.Equal(2, candidates.Count); // photo without any image URL is skipped, not fatal

        var first = candidates[0];
        Assert.Equal("https://images.pexels.com/large1.jpg", first.Url);
        Assert.Equal("https://images.pexels.com/medium1.jpg", first.ThumbUrl);
        Assert.Equal("Pexels", first.ProviderName);
        Assert.Equal("Pexels / John Doe", first.Attribution);
        Assert.Equal(4000, first.Width);
        Assert.Equal(3000, first.Height);

        var second = candidates[1];
        Assert.Equal("https://images.pexels.com/medium2.jpg", second.Url); // medium fallback
        Assert.Equal("Pexels / Jane", second.Attribution);
    }

    [Fact]
    public async Task Request_carries_the_authorization_header_and_query()
    {
        var (provider, handler) = CreateProvider();

        await provider.SearchAsync("mountain village", 2, CancellationToken.None);

        var request = handler.LastRequest!;
        var url = request.RequestUri!.AbsoluteUri;
        Assert.StartsWith("https://api.pexels.com/v1/search", url);
        Assert.Contains("query=mountain%20village", url);
        Assert.Contains("per_page=2", url);
        Assert.Equal("pex-key", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Empty_photos_produce_an_empty_list()
    {
        var (provider, _) = CreateProvider("""{"page": 1, "photos": []}""");

        var candidates = await provider.SearchAsync("nothing", 3, CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Unconfigured_provider_reports_itself_and_never_calls_the_api()
    {
        var (provider, handler) = CreateProvider(apiKey: "");

        Assert.False(provider.IsConfigured);
        Assert.Empty(await provider.SearchAsync("anything", 3, CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }
}
