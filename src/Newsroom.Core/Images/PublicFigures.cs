namespace Newsroom.Core.Images;

/// <summary>
/// One configured public figure the cover generator is allowed to depict (ADR-0012,
/// docs/05-integrations/images.md): a regional or national person whose likeness may appear in
/// a generated cover ONLY because an approved reference photo exists for them.
/// <paramref name="ReferenceImage"/> is the path to that photo (relative values resolve against
/// the worker's base directory); without a readable reference the figure is never drawn — a
/// name alone must never be turned into a likeness.
/// </summary>
/// <param name="Name">Canonical Bulgarian name, e.g. „Иван Иванов" — also what the drafting
/// model must echo back in <c>imageCentralPerson</c>.</param>
/// <param name="Role">Short Bulgarian role used in the image prompt, e.g. „кмет на Благоевград".</param>
/// <param name="Aliases">Extra spellings the sources use („кметът Иванов", a Latin
/// transliteration) — matched alongside <paramref name="Name"/>.</param>
public sealed record PublicFigure(
    string Name,
    string Role,
    string ReferenceImage,
    IReadOnlyList<string> Aliases)
{
    /// <summary>Name plus aliases, blanks dropped — everything that counts as a mention.</summary>
    public IEnumerable<string> AllNames =>
        Aliases.Prepend(Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim());
}

/// <summary>
/// The configured public-figure list plus the two pure lookups the pipeline needs: which figures
/// the source articles actually mention (fed to the drafting model as candidates), and which
/// configured figure a returned name refers to. No IO — loading the reference bytes is the
/// infrastructure layer's job.
/// </summary>
public sealed class PublicFigureDirectory(IReadOnlyList<PublicFigure> figures)
{
    public static readonly PublicFigureDirectory Empty = new([]);

    public IReadOnlyList<PublicFigure> Figures { get; } = figures;

    /// <summary>
    /// Figures explicitly named in <paramref name="text"/> (the topic's sources), in configured
    /// order. Matching is case-insensitive and bounded by non-letters, so „Иванов" does not fire
    /// on „Иванова" and a surname inside a longer word is not a mention.
    /// </summary>
    public IReadOnlyList<PublicFigure> Mentioned(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return Figures.Where(f => f.AllNames.Any(n => ContainsWhole(text, n))).ToList();
    }

    /// <summary>The configured figure with this exact name (or alias), or null — the guard that
    /// keeps a hallucinated name out of the prompt.</summary>
    public PublicFigure? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var needle = name.Trim();
        return Figures.FirstOrDefault(
            f => f.AllNames.Any(n => string.Equals(n, needle, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Whole-token containment: the match may not be flanked by letters or digits.</summary>
    private static bool ContainsWhole(string haystack, string needle)
    {
        if (needle.Length == 0)
            return false;

        var from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            var at = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;

            var beforeOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
            var end = at + needle.Length;
            var afterOk = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (beforeOk && afterOk)
                return true;

            from = at + 1;
        }
        return false;
    }
}

/// <summary>
/// A public figure the generator will actually depict on this cover: the configured person plus
/// the reference photo that has already been read and validated. Built by the image layer, never
/// by the model — <see cref="ReferenceIndex"/> is the 1-based position of the photo among the
/// prompt's reference images ("reference image 1").
/// </summary>
public sealed record CoverPersonBrief(string Name, string Role, int ReferenceIndex = 1);

/// <summary>Which corner of the cover the real Predel News logo is composited into after
/// generation (ADR-0013). The prompt asks the model to keep that corner clear, so the two stay
/// consistent when an operator moves the logo.</summary>
public enum CoverLogoCorner
{
    UpperRight,
    UpperLeft,
    LowerRight,
    LowerLeft,
}

/// <summary>
/// When a configured public figure may actually be drawn (ADR-0012). The drafting model has
/// already judged centrality; this is the editorial backstop that sits on top of it.
/// </summary>
public static class CoverPersonPolicy
{
    /// <summary>Categories where a recognisable face turns a report into an accusation. The
    /// default is a symbolic, event-focused cover instead; an operator can override per
    /// deployment (<c>Images:Cloudflare:AllowPublicFiguresInSensitiveCategories</c>) for the rare
    /// story where the figure is unquestionably the event.</summary>
    public static readonly IReadOnlyList<string> SensitiveCategories = ["Криминално"];

    public static bool IsSensitive(string? category) =>
        category is not null
        && SensitiveCategories.Contains(category.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>False when the story's category is sensitive and the override is off.</summary>
    public static bool MayDepict(string? category, bool allowInSensitiveCategories) =>
        allowInSensitiveCategories || !IsSensitive(category);
}
