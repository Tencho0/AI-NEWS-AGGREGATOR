using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class FacebookPostTests
{
    private static FacebookPost Post(string headline, string teaser) =>
        new(DraftId: 1, Headline: headline, Teaser: teaser, ArticleUrl: "https://predel.news/x");

    [Fact]
    public void DisplayTitle_returns_the_headline_when_present()
    {
        var post = Post("Общината обяви мерки", "Кука на поста.");

        Assert.Equal("Общината обяви мерки", post.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_falls_back_to_the_teasers_first_line_for_caption_posts()
    {
        var post = Post("", "Кука на поста.\n\nОще факти за събитието.");

        Assert.Equal("Кука на поста.", post.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_trims_the_first_line()
    {
        var post = Post("   ", "  Кука на поста.  \nВтори ред.");

        Assert.Equal("Кука на поста.", post.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_truncates_a_long_first_line_on_a_word_boundary()
    {
        var hook = string.Concat(Enumerable.Repeat("дума ", 20)).TrimEnd(); // 99 chars, plenty of spaces

        var title = Post("", hook).DisplayTitle;

        Assert.True(title.Length <= 81); // 80-char budget + ellipsis
        Assert.EndsWith("…", title);
        Assert.DoesNotContain(" …", title); // trimmed before the ellipsis, no dangling space
    }

    [Fact]
    public void DisplayTitle_hard_cuts_a_spaceless_first_line_over_80_chars()
    {
        var hook = new string('я', 90); // no spaces anywhere to break on

        var title = Post("", hook).DisplayTitle;

        Assert.Equal(new string('я', 79) + "…", title);
    }

    [Fact]
    public void DisplayTitle_of_an_exactly_80_char_first_line_is_not_truncated()
    {
        var hook = new string('я', 80);

        Assert.Equal(hook, Post("", hook).DisplayTitle);
    }
}
