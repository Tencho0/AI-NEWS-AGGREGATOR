using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class PublishTargetsTests
{
    [Theory]
    [InlineData("site", PublishTarget.Website)]
    [InlineData("fb", PublishTarget.Facebook)]
    public void TryParseCallbackToken_resolves_the_two_button_tokens(string token, PublishTarget expected)
    {
        Assert.True(PublishTargets.TryParseCallbackToken(token, out var target));
        Assert.Equal(expected, target);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("SITE")]
    [InlineData("website")]
    [InlineData("")]
    public void TryParseCallbackToken_rejects_anything_else(string token)
    {
        Assert.False(PublishTargets.TryParseCallbackToken(token, out _));
    }

    [Theory]
    [InlineData("Both", PublishTarget.Both)]
    [InlineData("Website", PublishTarget.Website)]
    [InlineData("Facebook", PublishTarget.Facebook)]
    public void Parse_reads_the_persisted_column_value(string persisted, PublishTarget expected)
    {
        Assert.Equal(expected, PublishTargets.Parse(persisted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("website")] // wrong case: persisted values are written with Name(), never lowercased
    public void Parse_falls_back_to_Both_so_a_bad_row_can_never_strand_a_draft(string? persisted)
    {
        Assert.Equal(PublishTarget.Both, PublishTargets.Parse(persisted));
    }

    [Fact]
    public void Umbraco_leg_serves_Both_and_Website_but_never_Facebook()
    {
        Assert.Equal(new[] { "Both", "Website" }, PublishTargets.UmbracoLeg);
    }

    [Fact]
    public void Facebook_link_leg_serves_only_Both()
    {
        Assert.Equal(new[] { "Both" }, PublishTargets.FacebookLinkLeg);
    }

    [Fact]
    public void Facebook_standalone_leg_serves_only_Facebook_normally()
    {
        Assert.Equal(new[] { "Facebook" }, PublishTargets.FacebookStandaloneLeg(facebookOnly: false));
    }

    [Fact]
    public void FacebookOnly_makes_the_standalone_leg_serve_every_target()
    {
        // Without this, a draft approved as Both — or scheduled with 📅, which always writes
        // Both — would wait forever under the flag for a site publish that never runs.
        Assert.Equal(
            new[] { "Both", "Website", "Facebook" },
            PublishTargets.FacebookStandaloneLeg(facebookOnly: true));
    }

    [Fact]
    public void Both_requires_the_site_and_the_page_when_Facebook_is_configured()
    {
        Assert.Equal(
            new[] { "umbraco", "facebook" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Both, facebookConfigured: true, facebookOnly: false));
    }

    [Fact]
    public void Both_requires_only_the_site_when_Facebook_is_not_configured()
    {
        Assert.Equal(
            new[] { "umbraco" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Both, facebookConfigured: false, facebookOnly: false));
    }

    [Fact]
    public void Website_reaches_Published_on_the_site_publish_alone()
    {
        // Not PartiallyPublished: Facebook is not in a website-only draft's required set.
        Assert.Equal(
            new[] { "umbraco" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Website, facebookConfigured: true, facebookOnly: false));
    }

    [Fact]
    public void Facebook_requires_only_the_page()
    {
        Assert.Equal(
            new[] { "facebook" },
            PublishTargets.RequiredDestinations(
                PublishTarget.Facebook, facebookConfigured: true, facebookOnly: false));
    }

    [Theory]
    [InlineData(PublishTarget.Both)]
    [InlineData(PublishTarget.Website)]
    [InlineData(PublishTarget.Facebook)]
    public void FacebookOnly_collapses_every_target_to_the_page(PublishTarget target)
    {
        Assert.Equal(
            new[] { "facebook" },
            PublishTargets.RequiredDestinations(target, facebookConfigured: true, facebookOnly: true));
    }
}
