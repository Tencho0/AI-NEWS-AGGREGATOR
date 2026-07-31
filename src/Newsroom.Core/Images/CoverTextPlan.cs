using System.Text;

namespace Newsroom.Core.Images;

/// <summary>Where the cover's text block sits. The composer turns this into an explicit
/// placement sentence; the scene prompt keeps the matching area visually calm.</summary>
public enum CoverTextPlacement
{
    /// <summary>Full-width band across the bottom third — the default news-cover look.</summary>
    LowerThird,
    LowerLeft,
    LowerRight,
    LeftThird,
    RightThird,
    UpperLeft,
}

/// <summary>Which element dominates the text block — the visual hierarchy the drafting model
/// chooses. <see cref="Number"/> makes the first key figure the largest element, which reads
/// harder in a feed when the story *is* the number.</summary>
public enum CoverTextEmphasis
{
    Headline,
    Number,
}

/// <summary>
/// The text FLUX.2 renders into the cover itself (ADR-0013): one short Bulgarian headline, up to
/// three very short key figures or highlights, plus the hierarchy and placement the drafting model
/// picked. Every string is length-capped and stripped of characters that would either break the
/// quoted prompt or produce garbled letterforms — long prose is never allowed near the image model.
///
/// The Predel News logo is deliberately absent: it is composited from the real asset after
/// generation so its shape is always exact (<c>ImageCompositor</c>).
/// </summary>
public sealed record CoverTextPlan(
    string Headline,
    IReadOnlyList<string> KeyPoints,
    CoverTextPlacement Placement = CoverTextPlacement.LowerThird,
    CoverTextEmphasis Emphasis = CoverTextEmphasis.Headline)
{
    /// <summary>Short enough to stay legible at feed thumbnail size and to keep the model from
    /// mangling a long Cyrillic string.</summary>
    public const int MaxHeadlineChars = 42;

    /// <summary>A key point is a figure or a two-word highlight — never a sentence.</summary>
    public const int MaxKeyPointChars = 18;

    public const int MaxKeyPoints = 3;

    /// <summary>Characters that must never reach the prompt: the double quote would close the
    /// quoted string, and the rest are either unrenderable or invite the model to invent
    /// punctuation-heavy captions.</summary>
    private static readonly char[] Forbidden =
    [
        '"', '\'', '\\', '{', '}', '<', '>', '|', '#', '*', '_', '`',
        '„', '“', '”', '«', '»', // „ " " « » — Bulgarian/typographic quotes
    ];

    /// <summary>False when normalization left nothing to render — the cover then stays strictly
    /// text-free rather than getting a half-empty text block.</summary>
    public bool HasText => Headline.Length > 0;

    /// <summary>
    /// Builds a plan from the drafting model's raw fields, or null when there is no usable
    /// headline. Unknown placement/emphasis words fall back to the defaults rather than failing
    /// the draft — a wrong layout is cosmetic, a failed draft is not.
    /// </summary>
    public static CoverTextPlan? From(
        string? headline,
        IEnumerable<string?>? keyPoints,
        string? placement = null,
        string? emphasis = null)
    {
        var cleanHeadline = Clean(headline, MaxHeadlineChars);
        if (cleanHeadline.Length == 0)
            return null;

        var cleanPoints = (keyPoints ?? [])
            .Select(p => Clean(p, MaxKeyPointChars))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxKeyPoints)
            .ToList();

        return new CoverTextPlan(
            cleanHeadline, cleanPoints, ParsePlacement(placement), ParseEmphasis(emphasis));
    }

    /// <summary>
    /// Re-applies the caps to an existing plan (the validator's normalize step). Returns the same
    /// instance when nothing changed, so <c>DraftValidator.Normalize</c> stays an identity
    /// operation on a compliant draft — compared field by field, because record equality on
    /// <see cref="KeyPoints"/> is reference equality and would always report a difference.
    /// </summary>
    public CoverTextPlan Normalized()
    {
        var normalized = From(Headline, KeyPoints, Placement.ToString(), Emphasis.ToString());
        if (normalized is null)
            return this;

        var unchanged = string.Equals(normalized.Headline, Headline, StringComparison.Ordinal)
            && normalized.Placement == Placement
            && normalized.Emphasis == Emphasis
            && normalized.KeyPoints.SequenceEqual(KeyPoints, StringComparer.Ordinal);
        return unchanged ? this : normalized;
    }

    /// <summary>Trims, collapses whitespace to single spaces, drops prompt-breaking characters,
    /// then truncates at a word boundary. Newlines collapse too — a cover headline is one line.</summary>
    private static string Clean(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var text = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value.Trim())
        {
            if (Forbidden.Contains(ch))
                continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && text.Length > 0)
                    text.Append(' ');
                lastWasSpace = true;
                continue;
            }
            lastWasSpace = false;
            text.Append(ch);
        }

        var cleaned = text.ToString().Trim();
        return cleaned.Length <= maxChars ? cleaned : TruncateAtWord(cleaned, maxChars);
    }

    private static string TruncateAtWord(string value, int maxChars)
    {
        var cut = value.LastIndexOf(' ', maxChars - 1);
        return (cut > 0 ? value[..cut] : value[..maxChars]).TrimEnd(',', ';', ':', '-', '—', '.', ' ');
    }

    /// <summary>Accepts the enum name in any casing plus the kebab/space forms the model is asked
    /// for ("lower-third", "lower third").</summary>
    private static CoverTextPlacement ParsePlacement(string? value) =>
        Enum.TryParse<CoverTextPlacement>(Compact(value), ignoreCase: true, out var parsed)
            ? parsed
            : CoverTextPlacement.LowerThird;

    private static CoverTextEmphasis ParseEmphasis(string? value) =>
        Enum.TryParse<CoverTextEmphasis>(Compact(value), ignoreCase: true, out var parsed)
            ? parsed
            : CoverTextEmphasis.Headline;

    private static string Compact(string? value) =>
        (value ?? "").Replace("-", "").Replace("_", "").Replace(" ", "").Trim();
}
