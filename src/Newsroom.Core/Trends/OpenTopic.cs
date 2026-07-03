namespace Newsroom.Core.Trends;

/// <summary>A non-Done topic as the trend scorer sees it (row in nw_Topic): the metadata the
/// status transitions need — current status, mute window and the label for alerts.</summary>
public sealed record OpenTopic(
    long TopicId,
    string Label,
    TopicStatus Status,
    DateTime? MutedUntilUtc);
