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
    private const int MinFacebookCaptionChars = 200;
    private const int MaxFacebookCaptionChars = 900;
    private const int MaxFacebookCaptionFirstLineChars = 120;
    private const double MaxFacebookCaptionUppercaseRatio = 0.6;
    private const int MaxFacebookHashtags = 3;

    /// <summary>
    /// Repairs cosmetic, safely-fixable overflows before validation: SEO title/description are
    /// truncated at a word boundary instead of failing the whole draft (models routinely
    /// overshoot these by a few characters — observed live 2026-07-03). Substantive problems
    /// (body, category, language) are never auto-repaired; those stay hard gates in Validate.
    /// </summary>
    public static DraftContent Normalize(DraftContent draft) => draft with
    {
        SeoTitle = TruncateAtWordBoundary(draft.SeoTitle.Trim(), MaxSeoTitleChars),
        SeoDescription = TruncateAtWordBoundary(draft.SeoDescription.Trim(), MaxSeoDescriptionChars),
        FacebookCaption = draft.FacebookCaption.Trim(),
        FacebookHashtags = NormalizeHashtags(draft.FacebookHashtags),
    };

    /// <summary>Repairs safely-fixable hashtag issues: missing leading '#', duplicates (case-
    /// insensitive), blanks, overflow beyond the cap. Malformed characters are NOT repaired —
    /// Validate flags those (a mangled hashtag is a content problem, not a cosmetic one).
    /// Returns the input list itself when it is already normalized, so Normalize stays an
    /// identity operation on compliant drafts (record equality included).</summary>
    private static IReadOnlyList<string> NormalizeHashtags(IReadOnlyList<string> hashtags)
    {
        var normalized = hashtags
            .Select(h => h.Trim().TrimStart('#'))
            .Where(h => h.Length > 0)
            .Select(h => "#" + h)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFacebookHashtags)
            .ToList();
        return normalized.SequenceEqual(hashtags, StringComparer.Ordinal) ? hashtags : normalized;
    }

    private static string TruncateAtWordBoundary(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        var cut = value.LastIndexOf(' ', maxChars);
        return (cut > 0 ? value[..cut] : value[..maxChars]).TrimEnd(',', ';', ':', '-', '—', ' ');
    }

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

        if (draft.FacebookCaption.Length < MinFacebookCaptionChars)
            violations.Add(
                $"Facebook caption is {draft.FacebookCaption.Length} chars (min {MinFacebookCaptionChars}).");
        else if (draft.FacebookCaption.Length > MaxFacebookCaptionChars)
            violations.Add(
                $"Facebook caption is {draft.FacebookCaption.Length} chars (max {MaxFacebookCaptionChars}).");
        else
        {
            var firstLine = draft.FacebookCaption.AsSpan(0, FirstLineLength(draft.FacebookCaption));
            if (firstLine.Length > MaxFacebookCaptionFirstLineChars)
                violations.Add(
                    $"Facebook caption first line is {firstLine.Length} chars (max {MaxFacebookCaptionFirstLineChars}) — the hook must fit above the fold.");

            if (draft.FacebookCaption.IndexOfAny(['*', '#']) >= 0)
                violations.Add("Facebook caption contains markdown/hashtag markers (* or #) — hashtags belong in facebookHashtags.");

            var uppercaseRatio = UppercaseLetterRatio(draft.FacebookCaption);
            if (uppercaseRatio > MaxFacebookCaptionUppercaseRatio)
                violations.Add(
                    $"Facebook caption is {uppercaseRatio:P0} uppercase (max {MaxFacebookCaptionUppercaseRatio:P0}) — no ALL CAPS on Facebook.");
        }

        if (draft.FacebookHashtags.Count > MaxFacebookHashtags)
            violations.Add(
                $"Draft has {draft.FacebookHashtags.Count} Facebook hashtags (max {MaxFacebookHashtags}).");
        foreach (var hashtag in draft.FacebookHashtags)
        {
            if (hashtag.Length < 2 || hashtag[0] != '#' || !hashtag[1..].All(char.IsLetterOrDigit))
                violations.Add($"Facebook hashtag '{hashtag}' is malformed (expected #дума, letters/digits only).");
        }

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

    private static int FirstLineLength(string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? text.Length : newline;
    }

    /// <summary>Share of uppercase among the cased letters of <paramref name="text"/>
    /// (0 when there are none) — the ALL-CAPS detector for the Facebook caption.</summary>
    private static double UppercaseLetterRatio(string text)
    {
        var cased = 0;
        var upper = 0;
        foreach (var ch in text)
        {
            if (char.IsUpper(ch))
            {
                cased++;
                upper++;
            }
            else if (char.IsLower(ch))
            {
                cased++;
            }
        }
        return cased == 0 ? 0 : (double)upper / cased;
    }
}
