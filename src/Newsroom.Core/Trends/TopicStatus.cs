namespace Newsroom.Core.Trends;

/// <summary>
/// Lifecycle of a topic (nw_Topic.Status). Emerging topics are being watched, Hot ones crossed
/// the trend threshold (docs/02-functional-spec.md §3), Muted ones are editor-suppressed but
/// still collect articles, Done ones fell out of the sliding window.
/// </summary>
public enum TopicStatus
{
    Emerging,
    Hot,
    Muted,
    Done
}
