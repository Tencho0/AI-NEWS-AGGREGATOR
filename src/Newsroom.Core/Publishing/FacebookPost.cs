namespace Newsroom.Core.Publishing;

/// <summary>
/// A pending Facebook page post shaped for the Graph API (docs/05-integrations/facebook.md):
/// the message becomes "{Headline}\n\n{Teaser}" and <see cref="ArticleUrl"/> — the live Umbraco
/// URL — becomes the link, so Facebook renders the OG card the site already composes.
/// </summary>
public sealed record FacebookPost(
    long DraftId,
    string Headline,
    string Teaser,
    string ArticleUrl);
