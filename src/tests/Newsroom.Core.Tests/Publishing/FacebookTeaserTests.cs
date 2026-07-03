using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class FacebookTeaserTests
{
    [Fact]
    public void The_seo_description_wins_when_present()
    {
        var teaser = FacebookTeaser.Compose(
            "Общината в Благоевград обяви нови мерки.", "Тяло, което не се ползва.");

        Assert.Equal("Общината в Благоевград обяви нови мерки.", teaser);
    }

    [Fact]
    public void The_seo_description_is_trimmed()
    {
        Assert.Equal("Резюме.", FacebookTeaser.Compose("  Резюме. \n", "Тяло."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_seo_description_falls_back_to_the_body(string? seoDescription)
    {
        Assert.Equal("Кратко тяло.", FacebookTeaser.Compose(seoDescription, "Кратко тяло."));
    }

    [Fact]
    public void Markdown_markers_are_stripped_from_the_body()
    {
        var teaser = FacebookTeaser.Compose(null,
            "## Новина\n\n**Общината** обяви *нови* мерки — виж [сайта](https://predel.news/x).");

        Assert.Equal("Новина Общината обяви нови мерки — виж сайта.", teaser);
    }

    [Fact]
    public void A_malformed_link_is_left_as_text()
    {
        Assert.Equal("виж [тук без URL.", FacebookTeaser.Compose(null, "виж [тук без URL."));
    }

    [Fact]
    public void Newlines_collapse_to_single_spaces()
    {
        Assert.Equal("Първи ред. Втори ред.", FacebookTeaser.Compose(null, "Първи ред.\n\nВтори ред."));
    }

    [Fact]
    public void A_long_body_is_cut_at_a_word_boundary_around_200_chars()
    {
        var body = string.Concat(Enumerable.Repeat("Общинската администрация съобщи за мерки. ", 10));

        var teaser = FacebookTeaser.Compose(null, body);

        Assert.True(teaser.Length <= FacebookTeaser.MaxBodyChars + 1); // +1 for the ellipsis
        Assert.EndsWith("…", teaser);
        // Cut on a word boundary: what precedes the ellipsis is a prefix of the body that
        // ends exactly where a word does.
        var prefix = teaser[..^1];
        Assert.StartsWith(prefix, body);
        Assert.Equal(' ', body[prefix.Length]);
    }

    [Fact]
    public void A_body_exactly_at_the_budget_is_not_cut()
    {
        var body = new string('а', FacebookTeaser.MaxBodyChars);

        Assert.Equal(body, FacebookTeaser.Compose(null, body));
    }
}
