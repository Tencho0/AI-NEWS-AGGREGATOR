namespace Newsroom.Core.Trends;

/// <summary>What the trend scorer needs to know about one article in a topic
/// (nw_TopicArticle joined to nw_SourceArticle + nw_ArticleAnalysis).</summary>
/// <param name="SeenAtUtc">The article's FirstSeenAtUtc — when we first saw the coverage.</param>
/// <param name="RegionScore">0..1 relevance to Southwest Bulgaria / Blagoevgrad.</param>
public sealed record TopicArticleFact(
    long TopicId,
    long ArticleId,
    int SourceId,
    DateTime SeenAtUtc,
    double RegionScore);
