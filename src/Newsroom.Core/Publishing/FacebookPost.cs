namespace Newsroom.Core.Publishing;

/// <summary>
/// A pending Facebook page post shaped for the Graph API (docs/05-integrations/facebook.md):
/// the message becomes "{Headline}\n\n{Teaser}" and <see cref="ArticleUrl"/> — the live Umbraco
/// URL — becomes the link, so Facebook renders the OG card the site already composes. When
/// <see cref="Image"/> is set (Facebook-only mode, no site link) the publisher posts a photo with
/// the message as its caption, so the image and the article land in one post; null falls back to
/// a text-only post.
/// </summary>
public sealed record FacebookPost(
    long DraftId,
    string Headline,
    string Teaser,
    string ArticleUrl,
    FacebookImage? Image = null);

/// <summary>The draft's chosen image for a Facebook photo post. Exactly one of <see cref="Url"/>
/// (a stock/library image Facebook fetches server-side) or <see cref="LocalPath"/> (an editor
/// upload on the worker's disk, sent as multipart bytes) is set. <see cref="FileName"/> names the
/// multipart part for uploads.</summary>
public sealed record FacebookImage(string? Url, string? LocalPath, string FileName);
