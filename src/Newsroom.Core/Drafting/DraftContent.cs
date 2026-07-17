namespace Newsroom.Core.Drafting;

/// <summary>
/// Everything the drafting model produces for one article draft (the JSON output contract in
/// docs/05-integrations/ai-generation.md). Validated by <see cref="DraftValidator"/> before it
/// may become a PendingReview draft.
/// </summary>
public sealed record DraftContent(
    string Headline,
    string? Subtitle,
    string BodyMarkdown,
    string Category,
    string? Region,
    IReadOnlyList<string> Tags,
    string SeoTitle,
    string SeoDescription,
    IReadOnlyList<string> ImageSearchQueries,
    string? ImageAltTextBg,
    IReadOnlyList<string> FlaggedClaims,
    double Confidence,
    string FacebookCaption,
    IReadOnlyList<string> FacebookHashtags);
