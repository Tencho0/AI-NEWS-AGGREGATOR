namespace Newsroom.Core.Drafting;

/// <summary>A Hot topic with its source articles, as handed to the drafting AI
/// (projection of nw_Topic + nw_TopicArticle + nw_SourceArticle + nw_ArticleAnalysis).</summary>
public sealed record TopicBundle(
    long TopicId,
    string Label,
    IReadOnlyList<TopicSourceArticle> Articles);

/// <summary>One source article inside a <see cref="TopicBundle"/>. <paramref name="Text"/> is
/// already truncated to the per-article prompt cap by the repository.</summary>
public sealed record TopicSourceArticle(
    long ArticleId,
    string Title,
    string SourceName,
    string Url,
    DateTime? PublishedAtUtc,
    string Summary,
    string? Text);
