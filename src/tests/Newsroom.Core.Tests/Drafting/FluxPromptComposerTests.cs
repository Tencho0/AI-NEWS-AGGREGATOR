using Newsroom.Core.Drafting;
using Newsroom.Core.Images;

namespace Newsroom.Core.Tests.Drafting;

public class FluxPromptComposerTests
{
    private const string Scene =
        "firefighters pulling a hose toward a smoking roof while neighbours watch from the street "
        + "on a grey winter afternoon";

    private static DraftContent Draft(
        IReadOnlyList<string>? queries = null,
        string headline = "НОВИ МЕРКИ В БЛАГОЕВГРАД",
        string category = "Общество",
        string? region = "Благоевград",
        string? scene = Scene,
        string? person = null,
        CoverTextPlan? coverText = null) => new(
        Headline: headline,
        Subtitle: null,
        BodyMarkdown: "Тяло на статията.",
        Category: category,
        Region: region,
        Tags: [],
        SeoTitle: "Нови мерки",
        SeoDescription: "Описание.",
        ImageSearchQueries: queries ?? ["city hall bulgaria", "municipal building"],
        ImageAltTextBg: "Сградата на общината",
        FlaggedClaims: [],
        Confidence: 0.8,
        FacebookCaption: "Кратък текст",
        FacebookHashtags: [],
        ImageScene: scene,
        ImagePersonName: person,
        CoverText: coverText);

    private static CoverTextPlan Cover(
        string headline = "ПОЖАР В ПЕТРИЧ",
        IReadOnlyList<string>? keyPoints = null,
        CoverTextPlacement placement = CoverTextPlacement.LowerThird,
        CoverTextEmphasis emphasis = CoverTextEmphasis.Headline) =>
        new(headline, keyPoints ?? ["3 сгради", "18 пожарникари"], placement, emphasis);

    [Fact]
    public void Asks_for_a_cinematic_photoreal_news_photograph_not_an_illustration()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("Cinematic photorealistic editorial news photograph", prompt);
        Assert.Contains("press photojournalist", prompt);
        Assert.DoesNotContain("not a photograph", prompt);
        Assert.DoesNotContain("magazine-cover editorial illustration", prompt);
    }

    [Fact]
    public void The_scene_is_the_subject_rendered_as_a_moment_unfolding()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        // Hook-system visual-action rule: the cover shows something happening, not a still life.
        Assert.Contains($"Scene, a real moment caught as it unfolds: {Scene}.", prompt);
        // The raw keyword queries must not be pasted in alongside a real scene.
        Assert.DoesNotContain("city hall bulgaria", prompt);
    }

    [Fact]
    public void A_missing_scene_falls_back_to_joining_the_image_search_queries()
    {
        var prompt = FluxPromptComposer.Compose(Draft(scene: null));

        Assert.Contains("city hall bulgaria; municipal building", prompt);
    }

    [Fact]
    public void A_bulgarian_scene_is_discarded_in_favour_of_the_english_queries()
    {
        var prompt = FluxPromptComposer.Compose(
            Draft(scene: "пожарникари гасят покрив на сграда в центъра на града"));

        Assert.DoesNotContain("пожарникари", prompt);
        Assert.Contains("city hall bulgaria; municipal building", prompt);
    }

    [Fact]
    public void Without_cover_text_the_headline_is_context_that_must_not_be_drawn()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("НОВИ МЕРКИ В БЛАГОЕВГРАД", prompt);
        Assert.Contains("never render it as text", prompt);
        Assert.Contains("Strictly no text", prompt);
    }

    [Fact]
    public void Cover_text_is_passed_as_exact_quoted_strings()
    {
        var prompt = FluxPromptComposer.Compose(Draft(coverText: Cover()));

        Assert.Contains("exactly these strings, spelled character for character", prompt);
        Assert.Contains("\"ПОЖАР В ПЕТРИЧ\"", prompt);
        Assert.Contains("\"3 сгради\", \"18 пожарникари\"", prompt);
        // With burnt-in text the article headline is no longer smuggled in as context.
        Assert.DoesNotContain("НОВИ МЕРКИ В БЛАГОЕВГРАД", prompt);
        Assert.DoesNotContain("Strictly no text", prompt);
    }

    [Fact]
    public void Cover_text_specifies_typography_alignment_colour_and_contrast()
    {
        var prompt = FluxPromptComposer.Compose(Draft(coverText: Cover()));

        Assert.Contains("heavy condensed contemporary sans-serif", prompt);
        Assert.Contains("Bulgarian Cyrillic", prompt);
        Assert.Contains("left-aligned", prompt);
        Assert.Contains("pure white type over a soft dark gradient scrim", prompt);
        Assert.Contains("high contrast and legibility at thumbnail size", prompt);
        Assert.Contains("at most one third of the frame", prompt);
    }

    [Fact]
    public void Headline_emphasis_makes_the_headline_the_largest_element()
    {
        var prompt = FluxPromptComposer.Compose(
            Draft(coverText: Cover(emphasis: CoverTextEmphasis.Headline)));

        Assert.Contains("Headline, the largest element", prompt);
        Assert.Contains("clearly smaller than the headline", prompt);
        Assert.Contains("directly beneath", prompt);
    }

    [Fact]
    public void Number_emphasis_flips_the_hierarchy_to_the_key_figure()
    {
        var prompt = FluxPromptComposer.Compose(
            Draft(coverText: Cover(emphasis: CoverTextEmphasis.Number)));

        Assert.Contains("Headline, secondary size", prompt);
        Assert.Contains("Key figures, the largest element", prompt);
        Assert.Contains("directly above", prompt);
    }

    [Theory]
    [InlineData(CoverTextPlacement.LowerThird, "the lower third", "left")]
    [InlineData(CoverTextPlacement.LowerLeft, "the lower-left quadrant", "left")]
    [InlineData(CoverTextPlacement.LowerRight, "the lower-right quadrant", "right")]
    [InlineData(CoverTextPlacement.RightThird, "the right third", "right")]
    [InlineData(CoverTextPlacement.UpperLeft, "the upper-left quadrant", "left")]
    public void Placement_drives_both_the_text_area_and_its_alignment(
        CoverTextPlacement placement, string area, string alignment)
    {
        var prompt = FluxPromptComposer.Compose(Draft(coverText: Cover(placement: placement)));

        Assert.Contains($"in {area}: \"ПОЖАР В ПЕТРИЧ\"", prompt);
        Assert.Contains($"keep {area} and", prompt); // the same area is kept calm in the scene
        Assert.Contains($"{alignment}-aligned", prompt);
    }

    [Fact]
    public void A_headline_only_cover_omits_the_key_figures_line()
    {
        var prompt = FluxPromptComposer.Compose(Draft(coverText: Cover(keyPoints: [])));

        Assert.Contains("\"ПОЖАР В ПЕТРИЧ\"", prompt);
        Assert.DoesNotContain("Key figures", prompt);
    }

    [Fact]
    public void A_plan_that_normalized_to_nothing_falls_back_to_a_text_free_cover()
    {
        var prompt = FluxPromptComposer.Compose(
            Draft(coverText: new CoverTextPlan("", [])));

        Assert.Contains("Strictly no text", prompt);
        Assert.DoesNotContain("exactly these strings", prompt);
    }

    [Fact]
    public void The_logo_is_never_generated_and_its_corner_is_reserved()
    {
        var withText = FluxPromptComposer.Compose(Draft(coverText: Cover()));
        var withoutText = FluxPromptComposer.Compose(Draft());

        Assert.Contains("no logo, wordmark, brand name or watermark anywhere", withText);
        Assert.Contains("least of all in the upper-right corner", withText);
        Assert.Contains("the upper-right corner visually calm", withText);
        Assert.Contains("no logo, no wordmark, no brand name, no watermark", withoutText);
    }

    [Fact]
    public void A_relocated_logo_corner_follows_through_to_the_prompt()
    {
        var prompt = FluxPromptComposer.Compose(
            Draft(coverText: Cover()), person: null, logoCorner: CoverLogoCorner.LowerRight);

        Assert.Contains("the lower-right corner visually calm", prompt);
        Assert.Contains("least of all in the lower-right corner", prompt);
        Assert.DoesNotContain("the upper-right corner", prompt);
    }

    [Fact]
    public void Places_the_story_in_its_real_southwest_bulgarian_setting()
    {
        var prompt = FluxPromptComposer.Compose(Draft(region: "Петрич"));

        Assert.Contains("Setting: Петрич in southwest Bulgaria (Pirin region)", prompt);
        Assert.Contains("architecture and terrain true to the area", prompt);
    }

    [Fact]
    public void A_missing_region_falls_back_to_the_generic_pirin_setting()
    {
        var prompt = FluxPromptComposer.Compose(Draft(region: null));

        Assert.Contains("Setting: a real town in the Pirin mountain region", prompt);
    }

    [Fact]
    public void The_category_only_tints_mood_and_colour()
    {
        var crime = FluxPromptComposer.Compose(Draft(category: "Криминално"));
        var weather = FluxPromptComposer.Compose(Draft(category: "Времето"));

        Assert.Contains("Mood and colour: tense low light", crime);
        Assert.Contains("Mood and colour: dramatic sky as the hero", weather);
        // The scene still comes from the article, never from the category.
        Assert.Contains(Scene, crime);
        Assert.Contains(Scene, weather);
    }

    [Fact]
    public void An_unknown_category_gets_the_default_mood()
    {
        var prompt = FluxPromptComposer.Compose(Draft(category: "Друго"));

        Assert.Contains("Mood and colour: natural daylight", prompt);
    }

    [Fact]
    public void Composes_for_a_16_9_feed_cover_with_depth_and_overlay_safe_areas()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("wide 16:9 frame", prompt);
        Assert.Contains("one clear primary subject", prompt);
        Assert.Contains("central safe area", prompt);
        Assert.Contains("foreground, middle ground and background", prompt);
        Assert.Contains("lower third and the upper-right corner visually calm", prompt);
    }

    [Fact]
    public void Asks_for_vivid_high_contrast_realism_and_bans_the_flat_poster_look()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("vivid saturated colour, high contrast", prompt);
        Assert.Contains("correct anatomy", prompt);
        Assert.Contains("flat vector or poster styling", prompt);
        Assert.Contains("empty lifeless scenes", prompt);
        Assert.Contains("muted washed-out palettes", prompt);
    }

    [Fact]
    public void Keeps_the_no_text_and_never_lurid_guardrails()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("no text", prompt);
        Assert.Contains("no letters", prompt);
        Assert.Contains("no logo", prompt);
        Assert.Contains("no numbers", prompt);
        Assert.Contains("No blood, gore or visible injuries", prompt);
        Assert.Contains("dramatic, never lurid", prompt);
    }

    [Fact]
    public void Without_an_approved_reference_nobody_in_the_frame_is_identifiable()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("ordinary fictional individuals", prompt);
        Assert.Contains("faces non-identifiable", prompt);
        Assert.Contains("Depict no real public figure", prompt);
        Assert.DoesNotContain("reference image", prompt);
    }

    [Fact]
    public void An_approved_reference_puts_the_named_figure_in_the_scene_under_hard_limits()
    {
        var prompt = FluxPromptComposer.Compose(
            Draft(), new CoverPersonBrief("Иван Иванов", "кмет на Благоевград"));

        Assert.Contains("the person in reference image 1 is Иван Иванов, кмет на Благоевград", prompt);
        Assert.Contains("from that reference only", prompt);
        Assert.Contains("Invent no action, location, gesture or circumstance", prompt);
        Assert.Contains("imply no guilt, arrest, detention, confrontation or misconduct", prompt);
        Assert.Contains("Everyone else in the frame is an ordinary fictional person", prompt);
    }

    [Fact]
    public void Blank_queries_and_no_scene_still_compose_a_usable_prompt()
    {
        var prompt = FluxPromptComposer.Compose(Draft(queries: ["", "   "], scene: null));

        Assert.DoesNotContain("Scene, a real moment", prompt);
        Assert.Contains("НОВИ МЕРКИ В БЛАГОЕВГРАД", prompt);
        Assert.Contains("no text", prompt);
        Assert.Contains("wide 16:9 frame", prompt);
    }

    [Fact]
    public void A_runaway_scene_is_trimmed_instead_of_the_prompt_being_cut_off()
    {
        // The scene is the only unbounded input, so it absorbs the budget — never the guardrails
        // at the end of the prompt, which a blind tail-truncation would delete.
        var prompt = FluxPromptComposer.Compose(
            Draft(scene: string.Join(" ", Enumerable.Repeat("firefighters", 400))));

        Assert.True(prompt.Length <= FluxPromptComposer.MaxPromptChars,
            $"prompt was {prompt.Length} chars");
        Assert.Contains("Strictly no text", prompt);
        Assert.Contains("dramatic, never lurid", prompt);
        Assert.Contains("Depict no real public figure", prompt);
    }

    [Fact]
    public void The_text_fences_survive_the_worst_case_prompt()
    {
        // Longest combination: burnt-in text, a public figure, and an over-long scene.
        var prompt = FluxPromptComposer.Compose(
            Draft(scene: string.Join(" ", Enumerable.Repeat("firefighters", 400)),
                coverText: Cover()),
            new CoverPersonBrief("Иван Иванов", "кмет на Благоевград"));

        Assert.True(prompt.Length <= FluxPromptComposer.MaxPromptChars,
            $"prompt was {prompt.Length} chars");
        Assert.Contains("\"ПОЖАР В ПЕТРИЧ\"", prompt);
        Assert.Contains("no logo, wordmark, brand name or watermark anywhere", prompt);
        Assert.Contains("imply no guilt", prompt);
        Assert.Contains("dramatic, never lurid", prompt);
    }
}
