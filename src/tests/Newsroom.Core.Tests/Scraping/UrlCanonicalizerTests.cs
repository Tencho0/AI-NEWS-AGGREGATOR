using Newsroom.Core.Scraping;

namespace Newsroom.Core.Tests.Scraping;

public class UrlCanonicalizerTests
{
    [Theory]
    // tracking params stripped, real params kept and sorted
    [InlineData(
        "https://Example.COM/news/story?utm_source=fb&utm_campaign=x&id=42&fbclid=abc",
        "https://example.com/news/story?id=42")]
    // fragment stripped
    [InlineData("https://example.com/a#section-2", "https://example.com/a")]
    // default port removed
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com:80/a", "http://example.com/a")]
    // non-default port kept
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a")]
    // param order stabilised
    [InlineData("https://example.com/a?b=2&a=1", "https://example.com/a?a=1&b=2")]
    // Cyrillic paths survive
    [InlineData("https://example.com/новини/статия-42", "https://example.com/%D0%BD%D0%BE%D0%B2%D0%B8%D0%BD%D0%B8/%D1%81%D1%82%D0%B0%D1%82%D0%B8%D1%8F-42")]
    public void Canonicalize_normalises(string input, string expected)
    {
        Assert.Equal(expected, UrlCanonicalizer.Canonicalize(input));
    }

    [Fact]
    public void Canonicalize_is_idempotent()
    {
        const string url = "https://Example.com/news?utm_source=x&z=1&a=2#frag";
        var once = UrlCanonicalizer.Canonicalize(url);
        Assert.Equal(once, UrlCanonicalizer.Canonicalize(once));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com/file")]
    public void TryCanonicalize_rejects_unusable_input(string input)
    {
        Assert.False(UrlCanonicalizer.TryCanonicalize(input, out _));
    }
}
