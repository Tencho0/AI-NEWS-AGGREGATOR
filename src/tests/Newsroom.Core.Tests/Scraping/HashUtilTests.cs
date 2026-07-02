using Newsroom.Core.Scraping;

namespace Newsroom.Core.Tests.Scraping;

public class HashUtilTests
{
    [Fact]
    public void ContentHash_ignores_whitespace_differences()
    {
        var a = HashUtil.ContentHash("Заглавие  на  статия", "Първи   абзац.\n\nВтори\tабзац.");
        var b = HashUtil.ContentHash("Заглавие на статия", " Първи абзац. Втори абзац. ");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ContentHash_differs_for_different_content()
    {
        var a = HashUtil.ContentHash("Заглавие", "Текст едно");
        var b = HashUtil.ContentHash("Заглавие", "Текст две");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ContentHash_keeps_title_and_body_separate()
    {
        // "AB" + "" must not collide with "A" + "B"
        var a = HashUtil.ContentHash("AB", "");
        var b = HashUtil.ContentHash("A", "B");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sha256Hex_is_lowercase_64_chars()
    {
        var hash = HashUtil.Sha256Hex("https://example.com/a");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }
}
