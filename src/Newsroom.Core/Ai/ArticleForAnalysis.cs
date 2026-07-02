namespace Newsroom.Core.Ai;

/// <summary>A scraped article as handed to the AI analysis stage (projection of nw_SourceArticle).</summary>
public sealed record ArticleForAnalysis(
    long ArticleId,
    string Title,
    string? Text,
    string SourceName,
    DateTime? PublishedAtUtc);
