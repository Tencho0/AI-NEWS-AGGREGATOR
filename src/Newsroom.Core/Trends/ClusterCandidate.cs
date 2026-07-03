namespace Newsroom.Core.Trends;

/// <summary>An analysed, not-yet-clustered article as handed to the clustering AI
/// (projection of nw_SourceArticle + nw_ArticleAnalysis).</summary>
public sealed record ClusterCandidate(
    long ArticleId,
    string Title,
    string Summary,
    IReadOnlyList<string> Entities);
