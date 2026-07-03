using Newsroom.Infrastructure.Images;

namespace Newsroom.Infrastructure.Tests.Images;

public class PixabayImageProviderTests
{
    private const string CannedJson =
        """
        {
          "total": 3,
          "totalHits": 3,
          "hits": [
            {"id": 1, "largeImageURL": "https://cdn.pixabay.com/large1.jpg",
             "webformatURL": "https://cdn.pixabay.com/web1.jpg",
             "previewURL": "https://cdn.pixabay.com/prev1.jpg",
             "user": "ivan", "imageWidth": 1920, "imageHeight": 1080},
            {"id": 2, "webformatURL": "https://cdn.pixabay.com/web2.jpg", "user": "maria"},
            {"id": 3, "user": "nobody", "tags": "hit without any image url"}
          ]
        }
        """;

    private static (PixabayImageProvider Provider, CannedResponseHandler Handler) CreateProvider(
        string json = CannedJson, string apiKey = "pix-key")
    {
        var handler = new CannedResponseHandler(json);
        var provider = new PixabayImageProvider(
            new FakeHttpClientFactory(handler), new ImagesOptions { PixabayApiKey = apiKey });
        return (provider, handler);
    }

    [Fact]
    public async Task Parses_hits_with_url_fallbacks_and_attribution()
    {
        var (provider, _) = CreateProvider();

        var candidates = await provider.SearchAsync("city hall", 3, CancellationToken.None);

        Assert.Equal(2, candidates.Count); // hit without any image URL is skipped, not fatal

        var first = candidates[0];
        Assert.Equal("https://cdn.pixabay.com/large1.jpg", first.Url);
        Assert.Equal("https://cdn.pixabay.com/prev1.jpg", first.ThumbUrl);
        Assert.Equal("Pixabay", first.ProviderName);
        Assert.Equal("Pixabay / ivan", first.Attribution);
        Assert.Equal(1920, first.Width);
        Assert.Equal(1080, first.Height);

        var second = candidates[1];
        Assert.Equal("https://cdn.pixabay.com/web2.jpg", second.Url); // webformat fallback
        Assert.Equal("https://cdn.pixabay.com/web2.jpg", second.ThumbUrl);
        Assert.Equal("Pixabay / maria", second.Attribution);
        Assert.Equal(0, second.Width);
    }

    [Fact]
    public async Task Request_carries_key_query_and_safety_parameters()
    {
        var (provider, handler) = CreateProvider();

        await provider.SearchAsync("city hall", 3, CancellationToken.None);

        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        Assert.StartsWith("https://pixabay.com/api/", url);
        Assert.Contains("key=pix-key", url);
        Assert.Contains("q=city%20hall", url);
        Assert.Contains("image_type=photo", url);
        Assert.Contains("safesearch=true", url);
        Assert.Contains("per_page=3", url);
    }

    [Fact]
    public async Task Empty_hits_produce_an_empty_list()
    {
        var (provider, _) = CreateProvider("""{"total": 0, "totalHits": 0, "hits": []}""");

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
