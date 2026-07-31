using Newsroom.Core.Images;

namespace Newsroom.Core.Drafting;

/// <summary>
/// Composes the English text-to-image prompt for the cover-image generator from a draft's own
/// details and nothing else (docs/05-integrations/images.md tier 3, ADR-0012, ADR-0013): the
/// drafting model's <see cref="DraftContent.ImageScene"/> — one coherent visual moment, not a bag
/// of keywords — carries the scene, the region places it, and the category tints mood and colour
/// without dictating what is depicted.
///
/// Style follows the marketing-skills plugin (image + ad-creative/hook-system skills): the image
/// skill's Subject + Setting + Style + Lighting + Composition + Technical order, natural sentences
/// rather than keyword soup (what FLUX responds to), and the hook system's "visual action" rule —
/// the cover's one job is to stop the thumb, so the scene must be a moment unfolding, vivid and
/// high-contrast, never a static establishing shot.
///
/// ADR-0012 moved the house style from flat illustration to cinematic photojournalistic realism.
/// ADR-0013 added the burnt-in headline and key figures: FLUX.2 renders
/// <see cref="DraftContent.CoverText"/> into the frame, quoted verbatim, with explicit placement,
/// hierarchy, typography, alignment, colour and contrast — and nothing else. Every rendered
/// character is therefore unverified until an editor sees it, which is exactly why the generated
/// cover goes to Telegram review before publication.
///
/// The editorial guardrails did not move: „никога жълто" — dramatic, never lurid or graphic; no
/// text beyond the quoted strings; **never** a logo, wordmark or watermark (the real Predel News
/// logo is composited from its own asset afterwards, so its shape is always exact); and no
/// recognisable real person unless an approved reference photo was supplied for them
/// (<see cref="CoverPersonBrief"/>) — a name alone never becomes a likeness. Everyone else is a
/// non-identifiable, fictional person.
/// </summary>
public static class FluxPromptComposer
{
    /// <summary>Cloudflare rejects prompts longer than 2048 characters (FLUX.1 Schnell's
    /// documented limit; kept as the ceiling for the FLUX.2 models too).</summary>
    public const int MaxPromptChars = 2048;

    /// <summary>Below this share of Cyrillic letters the scene counts as the English the drafting
    /// model was asked for; a Bulgarian scene is discarded in favour of the English queries.</summary>
    private const double MaxSceneCyrillicRatio = 0.3;

    private const string Opening =
        "Cinematic photorealistic editorial news photograph, shot like a press photojournalist "
        + "on assignment.";

    private const string StyleRules =
        "Look: vivid saturated colour, high contrast, crisp detail, candid reportage framing. "
        + "Real materials, correct anatomy, plausible clothing, vehicles and buildings.";

    private const string SafetyRules =
        "No blood, gore or visible injuries — dramatic, never lurid. Avoid empty lifeless scenes, "
        + "generic silhouettes, flat vector or poster styling, muted washed-out palettes, posed "
        + "studio arrangements and collage layouts.";

    /// <summary>Applies when the draft has no cover text — the pre-ADR-0013 rule, and still the
    /// rule whenever the drafting model gives us nothing renderable.</summary>
    private const string NoTextRules =
        "Strictly no text, no letters, no words, no numbers, no signage lettering, no logo, "
        + "no wordmark, no brand name, no watermark.";

    /// <summary>Nobody in the frame may be a real, identifiable person.</summary>
    private const string AnonymousPeopleRule =
        "People: ordinary fictional individuals, natural unposed body language, faces "
        + "non-identifiable — turned, distant or partly occluded. Depict no real public figure "
        + "and no recognisable real person.";

    private const string DefaultMood =
        "Mood and colour: natural daylight, grounded local atmosphere.";

    /// <summary>Mood/colour tint per article category (the drafting model's Bulgarian category
    /// names, Ai:Categories). Deliberately about light and palette only — the scene itself comes
    /// from the article, so a category never becomes a template. Unknown categories get
    /// <see cref="DefaultMood"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryMoods =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Общество"] = "Mood and colour: warm daylight, human-scale street life.",
            ["Политика"] = "Mood and colour: cool blue-grey light, formal civic weight, long shadows.",
            ["Икономика"] = "Mood and colour: clean amber and teal light, purposeful activity.",
            ["Криминално"] = "Mood and colour: tense low light, cool shadows cut by red-and-blue emergency glow, tension implied rather than shown.",
            ["Спорт"] = "Mood and colour: bright high-energy light, motion caught mid-action, saturated team colours.",
            ["Култура"] = "Mood and colour: rich warm stage and festival light, textured detail.",
            ["Здраве"] = "Mood and colour: clean soft light, calm teal-and-white palette.",
            ["Образование"] = "Mood and colour: bright optimistic morning light, warm friendly tones.",
            ["Времето"] = "Mood and colour: dramatic sky as the hero, volumetric light, weather at full scale.",
        };

    /// <summary>
    /// Builds the prompt. <paramref name="person"/> is set only when the pipeline has an approved
    /// reference photo attached to the request for a figure the article makes central; otherwise
    /// the frame stays anonymous. <paramref name="logoCorner"/> is kept free of generated content
    /// so the real logo can be composited there afterwards.
    /// </summary>
    public static string Compose(
        DraftContent content,
        CoverPersonBrief? person = null,
        CoverLogoCorner logoCorner = CoverLogoCorner.UpperRight)
    {
        var text = content.CoverText is { HasText: true } plan ? plan : null;

        // Everything except the scene is fixed-length-ish and non-negotiable: the composition,
        // the realism, the people rule, and above all the text fences (exact strings, nothing
        // else, never a logo). Those are assembled first and the scene — the only unbounded
        // input — gets whatever of the 2048-character budget is left. Blind tail-truncation would
        // be a correctness bug, not a cosmetic one: it can cut the fences off the end.
        var fixedParts = new List<string> { Opening, SettingFor(content.Region), MoodFor(content.Category) };
        if (text is null && !string.IsNullOrWhiteSpace(content.Headline))
            fixedParts.Add($"Story context, for understanding only — never render it as text: {content.Headline.Trim()}.");
        fixedParts.Add(CompositionRulesFor(text?.Placement, logoCorner));
        fixedParts.Add(StyleRules);
        fixedParts.Add(PeopleRuleFor(person));
        fixedParts.Add(text is null ? NoTextRules : TypographyRulesFor(text, logoCorner));
        fixedParts.Add(SafetyRules);

        // The hook-system "visual action" rule: the scene must be something happening now.
        var scene = SceneWithinBudget(SceneFor(content), fixedParts);
        var parts = scene.Length > 0
            ? [fixedParts[0], $"Scene, a real moment caught as it unfolds: {scene}.", .. fixedParts[1..]]
            : fixedParts;

        var prompt = string.Join(" ", parts);
        return prompt.Length <= MaxPromptChars ? prompt : prompt[..MaxPromptChars];
    }

    /// <summary>Truncates the scene at a word boundary to whatever the fixed parts leave over. A
    /// budget too small to say anything useful drops the scene entirely rather than emitting a
    /// fragment the model would misread.</summary>
    private static string SceneWithinBudget(string scene, IReadOnlyList<string> fixedParts)
    {
        if (scene.Length == 0)
            return "";

        const string opener = "Scene, a real moment caught as it unfolds: ";
        const int minimumUsefulScene = 40;

        var fixedLength = fixedParts.Sum(p => p.Length) + fixedParts.Count; // + one space each
        var budget = MaxPromptChars - fixedLength - opener.Length - 2; // '.' and the joining space
        if (budget < minimumUsefulScene)
            return "";
        if (scene.Length <= budget)
            return scene;

        var cut = scene.LastIndexOf(' ', Math.Min(budget, scene.Length - 1));
        return (cut > minimumUsefulScene ? scene[..cut] : scene[..budget])
            .TrimEnd(',', ';', ':', '-', '—', '.', ' ');
    }

    private static string SettingFor(string? region) =>
        string.IsNullOrWhiteSpace(region)
            ? "Setting: a real town in the Pirin mountain region of southwest Bulgaria — local "
              + "architecture and terrain true to the area."
            : $"Setting: {region.Trim()} in southwest Bulgaria (Pirin region) — local architecture "
              + "and terrain true to the area.";

    /// <summary>
    /// The drafting model's single English scene sentence is what the composer wants. It falls
    /// back to the stock <c>imageSearchQueries</c> only when that is missing or came back in
    /// Bulgarian — joined keywords make a weaker, less coherent frame, which is exactly why the
    /// scene field exists.
    /// </summary>
    private static string SceneFor(DraftContent content)
    {
        var scene = content.ImageScene?.Trim() ?? "";
        if (scene.Length > 0 && CyrillicLetterRatio(scene) <= MaxSceneCyrillicRatio)
            return scene.TrimEnd('.');

        return string.Join("; ", content.ImageSearchQueries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q.Trim()));
    }

    /// <summary>Composition, with the text area and the logo corner both asked to stay clear so
    /// the burnt-in type and the composited logo have somewhere to land.</summary>
    private static string CompositionRulesFor(CoverTextPlacement? placement, CoverLogoCorner logoCorner) =>
        "Composition: wide 16:9 frame; one clear primary subject just off-centre inside the central "
        + "safe area, with supporting elements from the same event; distinct foreground, middle "
        + "ground and background; natural depth of field; keep "
        + $"{AreaPhrase(placement)} and {CornerPhrase(logoCorner)} visually calm and uncluttered.";

    /// <summary>
    /// The burnt-in typography: exact quoted strings, explicit hierarchy, placement, alignment,
    /// colour and contrast — and a hard fence around everything else. The scrim is requested
    /// rather than assumed, because white type over an arbitrary photograph is a legibility
    /// gamble at feed size.
    /// </summary>
    private static string TypographyRulesFor(CoverTextPlan text, CoverLogoCorner logoCorner)
    {
        var headlineFirst = text.Emphasis == CoverTextEmphasis.Headline;
        var alignment = AlignmentFor(text.Placement);
        var lines = new List<string>
        {
            $"Render text into the image — exactly these strings, spelled character for character, "
            + $"and nothing else. Headline, {(headlineFirst ? "the largest element" : "secondary size")}, "
            + $"in {AreaPhrase(text.Placement)}: \"{text.Headline}\".",
        };

        if (text.KeyPoints.Count > 0)
        {
            var quoted = string.Join(", ", text.KeyPoints.Select(p => $"\"{p}\""));
            lines.Add(
                $"Key figures, {(headlineFirst ? "clearly smaller than the headline" : "the largest element")}, "
                + $"stacked {(headlineFirst ? "directly beneath" : "directly above")} it: {quoted}.");
        }

        lines.Add(
            "Typography: heavy condensed contemporary sans-serif, correct Bulgarian Cyrillic "
            + $"letterforms, tight leading, {alignment}-aligned, pure white type over a soft dark "
            + "gradient scrim for high contrast and legibility at thumbnail size.");
        lines.Add(
            "The text block fills at most one third of the frame, sits inside the safe margins and "
            + "never covers a face. Render no other text — no captions, sentences, paragraphs, dates "
            + "or source names — and no logo, wordmark, brand name or watermark anywhere, least of "
            + $"all in {CornerPhrase(logoCorner)}.");
        return string.Join(" ", lines);
    }

    /// <summary>
    /// With an approved reference photo the figure may be depicted — bounded hard: from the
    /// reference only, inside the scene the article supports, with no invented circumstance and
    /// no visual implication of guilt. Without one, nobody in the frame is identifiable.
    /// </summary>
    private static string PeopleRuleFor(CoverPersonBrief? person) =>
        person is null
            ? AnonymousPeopleRule
            : $"People: the person in reference image {person.ReferenceIndex} is {person.Name}, "
              + $"{person.Role} — render their likeness from that reference only, present in the "
              + "described scene, plausible everyday clothing, neutral professional expression. "
              + "Invent no action, location, gesture or circumstance beyond the scene, and imply no "
              + "guilt, arrest, detention, confrontation or misconduct. Everyone else in the frame "
              + "is an ordinary fictional person with a non-identifiable face.";

    private static string AreaPhrase(CoverTextPlacement? placement) => placement switch
    {
        CoverTextPlacement.LowerLeft => "the lower-left quadrant",
        CoverTextPlacement.LowerRight => "the lower-right quadrant",
        CoverTextPlacement.LeftThird => "the left third",
        CoverTextPlacement.RightThird => "the right third",
        CoverTextPlacement.UpperLeft => "the upper-left quadrant",
        _ => "the lower third",
    };

    private static string CornerPhrase(CoverLogoCorner corner) => corner switch
    {
        CoverLogoCorner.UpperLeft => "the upper-left corner",
        CoverLogoCorner.LowerRight => "the lower-right corner",
        CoverLogoCorner.LowerLeft => "the lower-left corner",
        _ => "the upper-right corner",
    };

    private static string AlignmentFor(CoverTextPlacement placement) => placement switch
    {
        CoverTextPlacement.LowerRight or CoverTextPlacement.RightThird => "right",
        _ => "left",
    };

    private static string MoodFor(string? category) =>
        category is not null && CategoryMoods.TryGetValue(category.Trim(), out var mood)
            ? mood
            : DefaultMood;

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
