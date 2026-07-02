namespace Newsroom.Core.Scraping;

public enum SourceArticleStatus
{
    New,
    Analysed,
    Ignored
}

/// <summary>
/// A scraped item (row in nw_SourceArticle). ExtractedText is an internal working copy for
/// analysis only — never republished (docs/06-security.md).
/// </summary>
public sealed record SourceArticle
{
    public long Id { get; init; }
    public required int SourceId { get; init; }
    /// <summary>Canonical URL (see <see cref="UrlCanonicalizer"/>); unique via UrlHash.</summary>
    public required string Url { get; init; }
    /// <summary>SHA-256 hex of <see cref="Url"/> — SQL Server can't uniquely index nvarchar(2000).</summary>
    public required string UrlHash { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public string? ExtractedText { get; init; }
    /// <summary>SHA-256 hex of normalised title+text; detects agency wire copy across sources.</summary>
    public required string ContentHash { get; init; }
    public SourceArticleStatus Status { get; init; } = SourceArticleStatus.New;
    public DateTime FirstSeenAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}
