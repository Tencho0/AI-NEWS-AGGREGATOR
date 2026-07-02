namespace Newsroom.Core.Scraping;

public enum SourceKind
{
    Rss,
    Sitemap,
    Html
}

/// <summary>A configured news source (row in nw_Source). Sources are data, not code.</summary>
public sealed record Source
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required SourceKind Kind { get; init; }
    public required string Url { get; init; }
    public string? ParserHint { get; init; }
    public int IntervalMinutes { get; init; } = 10;
    public bool Enabled { get; init; } = true;
    public int PolitenessDelaySeconds { get; init; } = 10;
    public DateTime? LastCrawledAtUtc { get; init; }
    public DateTime? LastSuccessAtUtc { get; init; }
    public string? LastError { get; init; }
    public int ConsecutiveFailures { get; init; }
    public string? Etag { get; init; }
    public string? LastModifiedHeader { get; init; }

    public bool IsDue(DateTime nowUtc) =>
        Enabled && (LastCrawledAtUtc is null
                    || LastCrawledAtUtc.Value.AddMinutes(IntervalMinutes) <= nowUtc);
}
