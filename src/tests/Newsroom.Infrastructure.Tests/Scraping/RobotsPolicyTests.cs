using Newsroom.Infrastructure.Scraping;

namespace Newsroom.Infrastructure.Tests.Scraping;

public class RobotsPolicyTests
{
    private const string Sample =
        """
        # comment
        User-agent: *
        Disallow: /admin/
        Disallow: /search
        Allow: /search/news

        User-agent: BadBot
        Disallow: /

        User-agent: PredelNewsBot
        Disallow: /private/
        """;

    [Theory]
    [InlineData("/novini/statia-1", true)]   // not mentioned → allowed
    [InlineData("/private/x", false)]        // our group disallows /private/
    [InlineData("/admin/x", true)]           // star group doesn't apply: our group exists
    public void Our_group_wins_over_star(string path, bool expected)
    {
        var rules = RobotsPolicy.Parse(Sample);
        Assert.Equal(expected, RobotsPolicy.IsAllowed(rules, path));
    }

    [Theory]
    [InlineData("/admin/x", false)]
    [InlineData("/search", false)]
    [InlineData("/search/news", true)]       // longer Allow beats shorter Disallow
    [InlineData("/novini", true)]
    public void Star_group_applies_when_no_specific_group(string path, bool expected)
    {
        const string starOnly =
            """
            User-agent: *
            Disallow: /admin/
            Disallow: /search
            Allow: /search/news
            """;
        var rules = RobotsPolicy.Parse(starOnly);
        Assert.Equal(expected, RobotsPolicy.IsAllowed(rules, path));
    }

    [Theory]
    [InlineData("/article.pdf", false)]      // wildcard + anchor
    [InlineData("/article.pdf?x=1", true)]   // $ anchors at end
    [InlineData("/a/tmp/file", false)]       // mid-path wildcard
    public void Wildcards_and_anchors(string path, bool expected)
    {
        const string content =
            """
            User-agent: *
            Disallow: /*.pdf$
            Disallow: /a/*/file
            """;
        var rules = RobotsPolicy.Parse(content);
        Assert.Equal(expected, RobotsPolicy.IsAllowed(rules, path));
    }

    [Fact]
    public void Empty_robots_allows_everything()
    {
        var rules = RobotsPolicy.Parse(string.Empty);
        Assert.True(RobotsPolicy.IsAllowed(rules, "/anything"));
    }

    [Fact]
    public void Grouped_user_agents_share_rules()
    {
        // consecutive UA lines form one group
        const string content =
            """
            User-agent: SomeBot
            User-agent: *
            Disallow: /x/
            """;
        var rules = RobotsPolicy.Parse(content);
        Assert.False(RobotsPolicy.IsAllowed(rules, "/x/page"));
    }
}
