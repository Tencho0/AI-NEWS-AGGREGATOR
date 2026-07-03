namespace Newsroom.Core.Trends;

/// <summary>The clustering verdict for one article: exactly one of
/// <paramref name="ExistingTopicId"/> (same concrete story already tracked) and
/// <paramref name="NewTopicLabel"/> (concise Bulgarian label for a new story) is set.</summary>
public sealed record ClusterAssignment(
    long ArticleId,
    long? ExistingTopicId,
    string? NewTopicLabel);
