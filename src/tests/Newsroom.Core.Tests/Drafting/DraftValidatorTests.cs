using Newsroom.Core.Drafting;

namespace Newsroom.Core.Tests.Drafting;

public class DraftValidatorTests
{
    private static readonly IReadOnlyList<string> Categories = ["Общество", "Политика", "Спорт"];
    private static readonly IReadOnlyList<string> Regions = ["Благоевград", "Петрич"];
    private static readonly DraftValidationOptions Options = new();

    /// <summary>~600 chars of plausible Bulgarian body text, comfortably inside the bounds.</summary>
    private static readonly string ValidBody = string.Concat(
        Enumerable.Repeat("Общинската администрация в Благоевград съобщи за нови мерки. ", 10));

    private static DraftContent ValidDraft() => new(
        Headline: "НОВИ МЕРКИ В БЛАГОЕВГРАД: ОБЩИНАТА ЗАТЯГА КОНТРОЛА",
        Subtitle: "Кметът обяви промените на брифинг",
        BodyMarkdown: ValidBody,
        Category: "Общество",
        Region: "Благоевград",
        Tags: ["Благоевград", "община", "мерки"],
        SeoTitle: "Нови мерки в Благоевград",
        SeoDescription: "Общината в Благоевград обяви нови мерки за контрол.",
        ImageSearchQueries: ["city hall bulgaria", "municipal building"],
        ImageAltTextBg: "Сградата на общината в Благоевград",
        FlaggedClaims: [],
        Confidence: 0.8);

    private static IReadOnlyList<string> Validate(DraftContent draft) =>
        DraftValidator.Validate(draft, Categories, Regions, Options);

    [Fact]
    public void Valid_draft_passes_with_no_violations()
    {
        Assert.Empty(Validate(ValidDraft()));
    }

    [Fact]
    public void Null_region_is_allowed()
    {
        Assert.Empty(Validate(ValidDraft() with { Region = null }));
    }

    [Fact]
    public void Empty_headline_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { Headline = "  " });
        Assert.Contains(violations, v => v.Contains("Headline"));
    }

    [Fact]
    public void Overlong_headline_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { Headline = new string('А', 201) });
        Assert.Contains(violations, v => v.Contains("Headline"));
    }

    [Fact]
    public void Too_short_body_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { BodyMarkdown = "Кратко." });
        Assert.Contains(violations, v => v.Contains("min 500"));
    }

    [Fact]
    public void Too_long_body_is_a_violation()
    {
        var body = string.Concat(Enumerable.Repeat("Дълъг текст на български език. ", 250));
        var violations = Validate(ValidDraft() with { BodyMarkdown = body });
        Assert.Contains(violations, v => v.Contains("max 6000"));
    }

    [Fact]
    public void Unknown_category_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { Category = "Хороскопи" });
        Assert.Contains(violations, v => v.Contains("Category 'Хороскопи'"));
    }

    [Fact]
    public void Unknown_region_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { Region = "София" });
        Assert.Contains(violations, v => v.Contains("Region 'София'"));
    }

    [Fact]
    public void Too_many_tags_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { Tags = ["а", "б", "в", "г", "д", "е", "ж"] });
        Assert.Contains(violations, v => v.Contains("tags"));
    }

    [Fact]
    public void Empty_tag_is_a_violation()
    {
        var violations = Validate(ValidDraft() with { Tags = ["Благоевград", " "] });
        Assert.Contains(violations, v => v.Contains("empty tag"));
    }

    [Fact]
    public void Overlong_seo_fields_are_violations()
    {
        var violations = Validate(ValidDraft() with
        {
            SeoTitle = new string('а', 71),
            SeoDescription = new string('а', 161),
        });
        Assert.Contains(violations, v => v.Contains("SEO title"));
        Assert.Contains(violations, v => v.Contains("SEO description"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Image_search_query_count_must_be_1_to_4(int count)
    {
        var queries = Enumerable.Range(1, count).Select(i => $"query {i}").ToList();
        var violations = Validate(ValidDraft() with { ImageSearchQueries = queries });
        Assert.Contains(violations, v => v.Contains("image search queries"));
    }

    [Fact]
    public void Mostly_latin_body_fails_the_cyrillic_ratio_gate()
    {
        var body = string.Concat(Enumerable.Repeat("This body is written in English, not Bulgarian. ", 15));
        var violations = Validate(ValidDraft() with { BodyMarkdown = body });
        Assert.Contains(violations, v => v.Contains("Cyrillic"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Confidence_outside_zero_to_one_is_a_violation(double confidence)
    {
        var violations = Validate(ValidDraft() with { Confidence = confidence });
        Assert.Contains(violations, v => v.Contains("Confidence"));
    }

    [Fact]
    public void Violations_accumulate_rather_than_short_circuit()
    {
        var violations = Validate(ValidDraft() with
        {
            Headline = "",
            Category = "Друго",
            Confidence = 2,
        });
        Assert.True(violations.Count >= 3);
    }
}

public class DraftNormalizerTests
{
    private static Newsroom.Core.Drafting.DraftContent Draft(string seoTitle, string seoDescription) => new(
        Headline: "ЗАГЛАВИЕ",
        Subtitle: null,
        BodyMarkdown: new string('т', 600),
        Category: "Общество",
        Region: null,
        Tags: ["тест"],
        SeoTitle: seoTitle,
        SeoDescription: seoDescription,
        ImageSearchQueries: ["city hall"],
        ImageAltTextBg: null,
        FlaggedClaims: [],
        Confidence: 0.9);

    [Fact]
    public void Normalize_truncates_overlong_seo_fields_at_word_boundary()
    {
        // 72-char title / 218-char description — the exact live failure from 2026-07-03
        var title = string.Join(' ', Enumerable.Repeat("дума", 14)) + " опашка";      // 76 chars
        var description = string.Join(' ', Enumerable.Repeat("описание", 24));        // 215 chars

        var normalized = Newsroom.Core.Drafting.DraftValidator.Normalize(Draft(title, description));

        Assert.True(normalized.SeoTitle.Length <= 70);
        Assert.True(normalized.SeoDescription.Length <= 160);
        Assert.False(normalized.SeoTitle.EndsWith(' '));
        Assert.DoesNotContain("дум ", normalized.SeoTitle + " ");                      // no mid-word cut
        Assert.Empty(Newsroom.Core.Drafting.DraftValidator.Validate(
            normalized, ["Общество"], [], new Newsroom.Core.Drafting.DraftValidationOptions()));
    }

    [Fact]
    public void Normalize_leaves_compliant_fields_untouched()
    {
        var draft = Draft("Кратко SEO заглавие", "Кратко SEO описание.");
        var normalized = Newsroom.Core.Drafting.DraftValidator.Normalize(draft);
        Assert.Equal(draft, normalized);
    }
}
