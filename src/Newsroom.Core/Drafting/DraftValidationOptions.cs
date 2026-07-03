namespace Newsroom.Core.Drafting;

/// <summary>Bounds for the <see cref="DraftValidator"/> quality gates
/// (docs/05-integrations/ai-generation.md). Defaults match the editorial style guide.</summary>
public sealed record DraftValidationOptions
{
    /// <summary>Shorter than this is a stub, not an article.</summary>
    public int MinBodyChars { get; init; } = 500;

    /// <summary>Longer than this the model is padding (style guide: 250-450 words).</summary>
    public int MaxBodyChars { get; init; } = 6000;

    public int MaxTags { get; init; } = 6;

    /// <summary>Bulgarian-language sanity check: minimum share of Cyrillic among letters.</summary>
    public double MinCyrillicRatio { get; init; } = 0.5;
}
