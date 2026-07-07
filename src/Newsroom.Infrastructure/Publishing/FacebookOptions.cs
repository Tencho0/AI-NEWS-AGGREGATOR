using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Publishing;

/// <summary>
/// Settings for Facebook page publishing (docs/05-integrations/facebook.md, ADR-0008), bound
/// from configuration: <c>Facebook:PageId</c>, <c>Facebook:AccessToken</c> (the long-lived page
/// token; fallback: the <c>FACEBOOK_PAGE_TOKEN</c> environment variable — secrets are not
/// configuration, docs/06-security.md), the Graph API version, the dry-run switch (default ON,
/// so a configured token never posts by surprise until DryRun is turned off deliberately) and
/// the attempt cap. The cycle cadence is owned by PublishJob (Umbraco:CheckSeconds).
/// </summary>
public sealed record FacebookOptions
{
    public string PageId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string GraphVersion { get; init; } = "v23.0";
    public bool DryRun { get; init; } = true;
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Whether the page post carries the article's live URL as the Graph <c>link</c>
    /// field (Facebook then renders the OG card). Off makes it a plain text post — used when the
    /// site link should not appear in the post.</summary>
    public bool IncludeLink { get; init; } = true;

    /// <summary>Manual one-shot test hook (<see cref="Publishing.FacebookTestPostService"/>): when
    /// &gt; 0, that draft id is posted to the page once on startup via the real publisher, with no
    /// Umbraco step. 0 (default) leaves the hook inert — production never sets it.</summary>
    public long TestPostDraftId { get; init; }

    /// <summary>The Facebook leg only runs with a page and a token to post with.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PageId) && !string.IsNullOrWhiteSpace(AccessToken);

    public static FacebookOptions From(IConfiguration configuration) => new()
    {
        PageId = configuration.GetValue("Facebook:PageId", "")!,
        AccessToken = ResolveAccessToken(configuration),
        GraphVersion = configuration.GetValue("Facebook:GraphVersion", "v23.0")!,
        DryRun = configuration.GetValue("Facebook:DryRun", true),
        MaxAttempts = configuration.GetValue("Facebook:MaxAttempts", 3),
        IncludeLink = configuration.GetValue("Facebook:IncludeLink", true),
        TestPostDraftId = configuration.GetValue("Facebook:TestPostDraftId", 0L),
    };

    private static string ResolveAccessToken(IConfiguration configuration)
    {
        var configured = configuration.GetValue("Facebook:AccessToken", "");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured!;
        return Environment.GetEnvironmentVariable("FACEBOOK_PAGE_TOKEN") ?? "";
    }
}
