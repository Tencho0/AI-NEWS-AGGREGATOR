namespace Newsroom.Core.Publishing;

/// <summary>
/// An Approved draft shaped for the site's publishing endpoint
/// (docs/05-integrations/umbraco.md): category/region/tags travel by name (the endpoint
/// resolves them to taxonomy nodes), and <see cref="DraftId"/> becomes the idempotency key
/// ("newsroom-draft-{DraftId}") so re-posting after a crash cannot duplicate an article.
/// </summary>
public sealed record ArticleToPublish(
    long DraftId,
    string Headline,
    string? Subtitle,
    string BodyMarkdown,
    string Category,
    string? Region,
    IReadOnlyList<string> Tags,
    string? SeoTitle,
    string? SeoDescription,
    PublishImage? Image);

/// <summary>The draft's chosen cover image. Stock picks carry the provider URL the site
/// fetches server-side; when the whole image is null the endpoint substitutes its configured
/// placeholder media item (the editor can swap it in the backoffice).</summary>
public sealed record PublishImage(
    string FileName,
    string? SourceUrl,
    string AltText,
    string? Attribution);
