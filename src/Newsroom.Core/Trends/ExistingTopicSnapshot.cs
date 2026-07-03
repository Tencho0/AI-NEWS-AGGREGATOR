namespace Newsroom.Core.Trends;

/// <summary>An open topic as shown to the clustering AI: its label plus the newest few article
/// titles, enough to recognise "same story" without resending every article.</summary>
public sealed record ExistingTopicSnapshot(
    long TopicId,
    string Label,
    IReadOnlyList<string> RecentTitles);
