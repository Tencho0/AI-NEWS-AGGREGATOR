namespace Newsroom.Core.Scraping;

/// <summary>Fetches a feed and parses its entries. 304-aware via the source's ETag/Last-Modified.</summary>
public interface IFeedReader
{
    /// <returns>Parsed items plus caching headers, or NotModified=true on HTTP 304.</returns>
    Task<FeedFetchResult> FetchAsync(Source source, CancellationToken ct);
}

public sealed record FeedFetchResult
{
    public bool NotModified { get; init; }
    public IReadOnlyList<FeedItem> Items { get; init; } = [];
    public string? Etag { get; init; }
    public string? LastModifiedHeader { get; init; }
}

/// <summary>Downloads an article page and extracts the main text (readability heuristic).</summary>
public interface IArticleTextExtractor
{
    /// <param name="parserHint">Optional per-source CSS selector (nw_Source.ParserHint).</param>
    Task<string?> ExtractAsync(string url, string? parserHint, CancellationToken ct);
}

/// <summary>robots.txt gate. Fail-open on missing robots (per REP), cached per host.</summary>
public interface IRobotsPolicy
{
    Task<bool> IsAllowedAsync(Uri url, CancellationToken ct);
}

public interface ISourceRepository
{
    Task<IReadOnlyList<Source>> GetDueAsync(DateTime nowUtc, CancellationToken ct);
    Task RecordSuccessAsync(int sourceId, DateTime nowUtc, string? etag, string? lastModifiedHeader, CancellationToken ct);
    Task RecordFailureAsync(int sourceId, DateTime nowUtc, string error, CancellationToken ct);
    /// <summary>Disables sources failing for longer than the cutoff. Returns those disabled.</summary>
    Task<IReadOnlyList<Source>> DisableDeadSourcesAsync(DateTime failingSinceUtc, CancellationToken ct);
}

public interface ISourceArticleRepository
{
    /// <summary>Insert if the canonical URL is new; update title/text when content changed. Never duplicates.</summary>
    /// <returns>true when a new row was inserted.</returns>
    Task<bool> UpsertAsync(SourceArticle article, CancellationToken ct);

    /// <summary>Which of the given URL hashes are already stored — lets the scraper skip
    /// full-page fetches for articles it has seen (politeness).</summary>
    Task<IReadOnlySet<string>> GetExistingUrlHashesAsync(IReadOnlyCollection<string> urlHashes, CancellationToken ct);
}
