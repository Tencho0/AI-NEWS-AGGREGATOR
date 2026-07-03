namespace Newsroom.Core.Drafting;

/// <summary>
/// Deterministic quality gates a draft must pass before it becomes PendingReview
/// (docs/05-integrations/ai-generation.md): schema shape is the AI layer's job, editorial
/// bounds and taxonomy membership are checked here — pure code, no AI cost.
/// </summary>
public static class DraftValidator
{
    private const int MaxHeadlineChars = 200;
    private const int MaxSeoTitleChars = 70;
    private const int MaxSeoDescriptionChars = 160;
    private const int MaxImageSearchQueries = 4;

    /// <summary>Returns violation messages; an empty list means the draft is valid.</summary>
    public static IReadOnlyList<string> Validate(
        DraftContent draft,
        IReadOnlyList<string> allowedCategories,
        IReadOnlyList<string> allowedRegions,
        DraftValidationOptions options)
    {
        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(draft.Headline))
            violations.Add("Headline is empty.");
        else if (draft.Headline.Length > MaxHeadlineChars)
            violations.Add($"Headline is {draft.Headline.Length} chars (max {MaxHeadlineChars}).");

        if (draft.BodyMarkdown.Length < options.MinBodyChars)
            violations.Add($"Body is {draft.BodyMarkdown.Length} chars (min {options.MinBodyChars}).");
        else if (draft.BodyMarkdown.Length > options.MaxBodyChars)
            violations.Add($"Body is {draft.BodyMarkdown.Length} chars (max {options.MaxBodyChars}).");

        if (!allowedCategories.Contains(draft.Category, StringComparer.Ordinal))
            violations.Add($"Category '{draft.Category}' is not in the site taxonomy.");

        if (draft.Region is not null && !allowedRegions.Contains(draft.Region, StringComparer.Ordinal))
            violations.Add($"Region '{draft.Region}' is not in the site taxonomy.");

        if (draft.Tags.Count > options.MaxTags)
            violations.Add($"Draft has {draft.Tags.Count} tags (max {options.MaxTags}).");
        if (draft.Tags.Any(string.IsNullOrWhiteSpace))
            violations.Add("Draft contains an empty tag.");

        if (draft.SeoTitle.Length > MaxSeoTitleChars)
            violations.Add($"SEO title is {draft.SeoTitle.Length} chars (max {MaxSeoTitleChars}).");
        if (draft.SeoDescription.Length > MaxSeoDescriptionChars)
            violations.Add($"SEO description is {draft.SeoDescription.Length} chars (max {MaxSeoDescriptionChars}).");

        if (draft.ImageSearchQueries.Count is < 1 or > MaxImageSearchQueries)
            violations.Add(
                $"Draft has {draft.ImageSearchQueries.Count} image search queries (need 1..{MaxImageSearchQueries}).");

        var cyrillicRatio = CyrillicLetterRatio(draft.BodyMarkdown);
        if (cyrillicRatio < options.MinCyrillicRatio)
            violations.Add(
                $"Body is only {cyrillicRatio:P0} Cyrillic (min {options.MinCyrillicRatio:P0}) — not Bulgarian?");

        if (draft.Confidence is < 0 or > 1 || double.IsNaN(draft.Confidence))
            violations.Add($"Confidence {draft.Confidence} is outside [0, 1].");

        return violations;
    }

    /// <summary>Share of Cyrillic among the letters of <paramref name="text"/> (0 when letterless).</summary>
    private static double CyrillicLetterRatio(string text)
    {
        var letters = 0;
        var cyrillic = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch))
                continue;
            letters++;
            if (ch is >= 'Ѐ' and <= 'ӿ') // Unicode Cyrillic block
                cyrillic++;
        }
        return letters == 0 ? 0 : (double)cyrillic / letters;
    }
}
