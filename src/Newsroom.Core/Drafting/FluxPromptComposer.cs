namespace Newsroom.Core.Drafting;

/// <summary>
/// Composes the English text-to-image prompt for the cover-image generator from a draft's own
/// details and nothing else (docs/05-integrations/images.md tier 3, ADR-0011): the drafting
/// model's English image-search queries carry the subject, the headline and region add context,
/// and a category-specific mood line sets lighting and palette.
///
/// Template follows the marketing-skills plugin (image, ad-creative + hook-system,
/// marketing-psychology skills): the image skill's Subject + Setting + Style + Lighting +
/// Composition + Technical formula in keyword phrases, well under the ~200-word focus limit;
/// the hook system's "visual action" rule — the cover's one job is to stop the thumb, so the
/// subject is asked for as a moment of action (something has just happened or is unfolding),
/// which is the curiosity gap the headline then anchors; and mere-exposure branding — one
/// consistent illustration style across all covers. The editorial guardrails stay hard:
/// „никога жълто" — dramatic, never lurid (the hook system's own warning: a clickbait
/// thumbstop poisons trust); clearly an illustration, never photo-realistic; no real
/// identifiable people; no embedded text (diffusion models garble letters, Cyrillic doubly so).
/// </summary>
public static class FluxPromptComposer
{
    /// <summary>FLUX.1 Schnell rejects prompts longer than 2048 characters.</summary>
    public const int MaxPromptChars = 2048;

    private const string CompositionRules =
        "Composition: one dominant off-center focal subject with a bold silhouette that reads "
        + "at thumbnail size, cinematic angle, strong foreground-background depth, one vivid "
        + "accent colour against a restrained palette.";

    private const string StyleRules =
        "Style: premium magazine-cover editorial illustration, clean confident shapes, rich "
        + "atmosphere, clearly an illustration, not a photograph.";

    private const string TechnicalRules =
        "Strictly no text, no letters, no words, no numbers, no logos, no watermarks. "
        + "No real identifiable people, no photo-realistic faces. "
        + "No graphic violence, no blood — dramatic, never lurid.";

    private const string DefaultMood =
        "Mood: atmospheric natural light, grounded local feeling.";

    /// <summary>Lighting/palette keywords per article category (the drafting model's Bulgarian
    /// category names, Ai:Categories). Unknown categories get <see cref="DefaultMood"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryMoods =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Общество"] = "Mood: golden-hour warmth, human-scale street life, sense of community.",
            ["Политика"] = "Mood: imposing civic architecture, deep blue and grey palette, long shadows, quiet tension.",
            ["Икономика"] = "Mood: dynamic geometric energy, amber and teal palette, upward motion.",
            ["Криминално"] = "Mood: tense night scene, cool blue shadows, red-and-blue emergency-light glow, suggestive silhouettes, implied not explicit.",
            ["Спорт"] = "Mood: peak action frozen mid-motion, low heroic angle, vibrant saturated colours.",
            ["Култура"] = "Mood: festive theatrical light, rich warm colours, artistic texture.",
            ["Здраве"] = "Mood: calm and hopeful, soft diffused light, fresh teal-and-white palette.",
            ["Образование"] = "Mood: bright optimistic morning light, warm friendly colours.",
            ["Времето"] = "Mood: towering dramatic sky as the hero, volumetric light, extreme weather over mountain ridges, awe-inspiring scale.",
        };

    public static string Compose(DraftContent content)
    {
        var subject = string.Join("; ", content.ImageSearchQueries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim()));

        var parts = new List<string>
        {
            "Striking editorial news cover illustration.",
        };
        // Hook-system "visual action" rule: a moment of action stops the thumb; a static
        // scene of the same nouns does not.
        if (subject.Length > 0)
            parts.Add($"Subject, caught mid-moment as something happens: {subject}.");
        parts.Add(string.IsNullOrWhiteSpace(content.Region)
            ? "Setting: a small town in the Pirin mountain region of southwest Bulgaria."
            : $"Setting: {content.Region.Trim()}, a town in the Pirin mountain region of southwest Bulgaria.");
        if (!string.IsNullOrWhiteSpace(content.Headline))
            parts.Add($"Story context, never render as text: {content.Headline.Trim()}.");
        parts.Add(MoodFor(content.Category));
        parts.Add(CompositionRules);
        parts.Add(StyleRules);
        parts.Add(TechnicalRules);

        var prompt = string.Join(" ", parts);
        return prompt.Length <= MaxPromptChars ? prompt : prompt[..MaxPromptChars];
    }

    private static string MoodFor(string? category) =>
        category is not null && CategoryMoods.TryGetValue(category.Trim(), out var mood)
            ? mood
            : DefaultMood;
}
