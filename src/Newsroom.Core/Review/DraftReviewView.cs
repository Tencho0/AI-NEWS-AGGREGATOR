namespace Newsroom.Core.Review;

/// <summary>
/// Everything needed to render (and later edit) one review message for a draft — a projection
/// of nw_Draft + nw_Topic (docs/05-integrations/telegram.md "Review message format").
/// <paramref name="TelegramMessageId"/> is null until the message is dispatched; resolved-state
/// edits (approve/reject/changes/expiry) re-render this view and append a status suffix.
/// </summary>
public sealed record DraftReviewView(
    long DraftId,
    int Version,
    string TopicLabel,
    double TopicScore,
    int SourceCount,
    string Headline,
    string? Subtitle,
    string BodyMarkdown,
    string Category,
    string? Region,
    IReadOnlyList<string> Tags,
    IReadOnlyList<(string Name, string Url)> Sources,
    IReadOnlyList<string> FlaggedClaims,
    double? Confidence,
    decimal Cost,
    string? Model,
    int ImageCount,
    long? TelegramMessageId);
