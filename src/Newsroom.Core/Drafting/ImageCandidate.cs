namespace Newsroom.Core.Drafting;

/// <summary>One stock-photo suggestion for a draft (docs/05-integrations/images.md, ADR-0009),
/// carrying the attribution the licence requires.</summary>
public sealed record ImageCandidate(
    string Url,
    string? ThumbUrl,
    string ProviderName,
    string? Attribution,
    int Width,
    int Height);
