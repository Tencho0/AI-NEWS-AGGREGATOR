using Newsroom.Core.Images;

namespace Newsroom.Core.Drafting;

/// <summary>
/// Everything the drafting model produces for one article draft (the JSON output contract in
/// docs/05-integrations/ai-generation.md). Validated by <see cref="DraftValidator"/> before it
/// may become a PendingReview draft.
/// </summary>
/// <param name="ImageScene">One coherent English sentence describing the single visual moment the
/// cover should show — subject, place, time of day, weather (ADR-0012). Drives
/// <see cref="FluxPromptComposer"/>; null falls back to joining <paramref name="ImageSearchQueries"/>,
/// which composes a weaker frame.</param>
/// <param name="ImagePersonName">The configured public figure the model judged central to the
/// event, echoed back from the candidates it was given — or null (only mentioned in passing, none
/// matched, or a sensitive story where a symbolic scene is safer). Never a likeness on its own:
/// the image layer still requires an approved reference photo for that name.</param>
/// <param name="CoverText">The short Bulgarian headline and up to three key figures FLUX.2 burns
/// into the cover, with the hierarchy and placement the model chose (ADR-0013). Null — or a plan
/// whose headline normalized away — means a strictly text-free cover. Never carries the logo: that
/// is composited from the real asset afterwards.</param>
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
    IReadOnlyList<string> FacebookHashtags,
    string? ImageScene = null,
    string? ImagePersonName = null,
    CoverTextPlan? CoverText = null);
