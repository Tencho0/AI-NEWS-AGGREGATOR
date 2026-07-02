namespace Newsroom.Core.Scraping;

/// <summary>One entry parsed from an RSS/Atom feed, before canonicalisation and storage.</summary>
public sealed record FeedItem
{
    public required string Link { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    /// <summary>Plain-text body: content:encoded when present, else the summary. May be short.</summary>
    public string? Text { get; init; }
}
