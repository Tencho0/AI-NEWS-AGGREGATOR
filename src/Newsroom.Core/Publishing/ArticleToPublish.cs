namespace Newsroom.Core.Publishing;

/// <summary>
/// An Approved draft shaped for the site's publishing endpoint
/// (docs/05-integrations/umbraco.md): category/region/tags travel by name (the endpoint
/// resolves them to taxonomy nodes), and <see cref="PublishRef"/> becomes the idempotency key
/// ("newsroom-{PublishRef:N}") so re-posting after a crash cannot duplicate an article.
/// The ref is a GUID minted at draft creation, NOT the row id — identity values restart when
/// a database is rebuilt while the site's ledger persists, and an id-based key then collides
/// with a previous life's entry, silently returning the wrong article (hit live 2026-07-03).
/// </summary>
public sealed record ArticleToPublish(
    long DraftId,
    Guid PublishRef,
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
/// fetches server-side; editor uploads carry <paramref name="LocalPath"/> instead — a worker
/// file the publisher inlines as base64 at publish time (Core stays IO-free). Exactly one of
/// SourceUrl/LocalPath is set. When the whole image is null the endpoint substitutes its
/// configured placeholder media item (the editor can swap it in the backoffice).</summary>
public sealed record PublishImage(
    string FileName,
    string? SourceUrl,
    string AltText,
    string? Attribution,
    string? LocalPath);
