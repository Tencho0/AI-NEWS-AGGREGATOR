using Newsroom.Core.Drafting;

namespace Newsroom.Core.Tests.Drafting;

public class FluxPromptComposerTests
{
    private static DraftContent Draft(
        IReadOnlyList<string>? queries = null,
        string headline = "НОВИ МЕРКИ В БЛАГОЕВГРАД",
        string category = "Общество",
        string? region = "Благоевград") => new(
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
        FacebookHashtags: []);

    [Fact]
    public void Includes_the_image_search_queries_as_a_mid_action_subject()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        // Hook-system visual-action rule: the subject is requested as a moment of action.
        Assert.Contains("caught mid-moment as something happens: city hall bulgaria; municipal building.", prompt);
    }

    [Fact]
    public void Includes_the_headline_as_context()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("НОВИ МЕРКИ В БЛАГОЕВГРАД", prompt);
        Assert.Contains("never render as text", prompt);
    }

    [Fact]
    public void Includes_the_region_as_the_setting()
    {
        var prompt = FluxPromptComposer.Compose(Draft(region: "Петрич"));

        Assert.Contains("Setting: Петрич", prompt);
    }

    [Fact]
    public void A_missing_region_falls_back_to_the_generic_pirin_setting()
    {
        var prompt = FluxPromptComposer.Compose(Draft(region: null));

        Assert.Contains("Setting: a small town in the Pirin mountain region", prompt);
    }

    [Fact]
    public void The_category_selects_the_mood_line()
    {
        var crime = FluxPromptComposer.Compose(Draft(category: "Криминално"));
        var weather = FluxPromptComposer.Compose(Draft(category: "Времето"));

        Assert.Contains("emergency-light glow", crime);
        Assert.Contains("implied not explicit", crime);
        Assert.Contains("towering dramatic sky", weather);
    }

    [Fact]
    public void An_unknown_category_gets_the_default_mood()
    {
        var prompt = FluxPromptComposer.Compose(Draft(category: "Друго"));

        Assert.Contains("Mood: atmospheric natural light", prompt);
    }

    [Fact]
    public void Enforces_the_no_text_illustration_and_editorial_safety_directives()
    {
        var prompt = FluxPromptComposer.Compose(Draft());

        Assert.Contains("no text", prompt);
        Assert.Contains("no letters", prompt);
        Assert.Contains("illustration", prompt);
        Assert.Contains("No real identifiable people", prompt);
        Assert.Contains("No graphic violence", prompt);
    }

    [Fact]
    public void Blank_queries_are_skipped_and_the_prompt_still_composes()
    {
        var prompt = FluxPromptComposer.Compose(Draft(queries: ["", "   "]));

        Assert.DoesNotContain("Subject:", prompt);
        Assert.Contains("НОВИ МЕРКИ В БЛАГОЕВГРАД", prompt);
        Assert.Contains("no text", prompt);
    }

    [Fact]
    public void Long_prompts_are_capped_at_the_flux_limit()
    {
        var longQuery = new string('a', 3000);
        var prompt = FluxPromptComposer.Compose(Draft(queries: [longQuery]));

        Assert.Equal(FluxPromptComposer.MaxPromptChars, prompt.Length);
    }
}
