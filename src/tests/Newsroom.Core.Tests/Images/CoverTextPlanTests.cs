using Newsroom.Core.Images;

namespace Newsroom.Core.Tests.Images;

public class CoverTextPlanTests
{
    [Fact]
    public void Builds_a_plan_from_the_models_fields()
    {
        var plan = CoverTextPlan.From(
            "ПОЖАР В ПЕТРИЧ", ["3 сгради", "18 пожарникари"], "lower-left", "number");

        Assert.NotNull(plan);
        Assert.Equal("ПОЖАР В ПЕТРИЧ", plan.Headline);
        Assert.Equal(["3 сгради", "18 пожарникари"], plan.KeyPoints);
        Assert.Equal(CoverTextPlacement.LowerLeft, plan.Placement);
        Assert.Equal(CoverTextEmphasis.Number, plan.Emphasis);
        Assert.True(plan.HasText);
    }

    [Fact]
    public void No_headline_means_no_plan_and_therefore_a_text_free_cover()
    {
        Assert.Null(CoverTextPlan.From(null, ["3 сгради"]));
        Assert.Null(CoverTextPlan.From("", ["3 сгради"]));
        Assert.Null(CoverTextPlan.From("   ", null));
        // Nothing but forbidden characters is the same as nothing.
        Assert.Null(CoverTextPlan.From("\"\"\"", null));
    }

    [Fact]
    public void Key_points_are_optional()
    {
        var plan = CoverTextPlan.From("НОВ МОСТ В СИМИТЛИ", null);

        Assert.NotNull(plan);
        Assert.Empty(plan.KeyPoints);
    }

    [Fact]
    public void Quotes_and_prompt_breaking_characters_are_stripped()
    {
        // A stray double quote would close the quoted string in the image prompt.
        var plan = CoverTextPlan.From("\"ПОЖАР\" в „Петрич\"", ["*3* сгради"]);

        Assert.NotNull(plan);
        Assert.Equal("ПОЖАР в Петрич", plan.Headline);
        Assert.Equal(["3 сгради"], plan.KeyPoints);
        Assert.DoesNotContain('"', plan.Headline);
    }

    [Fact]
    public void Newlines_and_runs_of_whitespace_collapse_to_single_spaces()
    {
        var plan = CoverTextPlan.From("ПОЖАР\n\n   В    ПЕТРИЧ", null);

        Assert.Equal("ПОЖАР В ПЕТРИЧ", plan!.Headline);
    }

    [Fact]
    public void A_long_headline_is_truncated_at_a_word_boundary()
    {
        var plan = CoverTextPlan.From(
            "ОБЩИНСКИЯТ СЪВЕТ ПРИЕ БЮДЖЕТА ЗА СЛЕДВАЩАТА ГОДИНА С ПЪЛНО МНОЗИНСТВО", null);

        Assert.NotNull(plan);
        Assert.True(plan.Headline.Length <= CoverTextPlan.MaxHeadlineChars);
        Assert.DoesNotContain("  ", plan.Headline);
        // Cut on a space, not mid-word.
        Assert.False(plan.Headline.EndsWith(' '));
    }

    [Fact]
    public void Key_points_are_capped_in_count_and_length()
    {
        var plan = CoverTextPlan.From(
            "БЮДЖЕТ 2027",
            ["12 млн. лв.", "3 нови обекта", "18 месеца срок", "четвърти акцент който е излишен"]);

        Assert.NotNull(plan);
        Assert.Equal(CoverTextPlan.MaxKeyPoints, plan.KeyPoints.Count);
        Assert.All(plan.KeyPoints, p => Assert.True(p.Length <= CoverTextPlan.MaxKeyPointChars));
    }

    [Fact]
    public void Blank_and_duplicate_key_points_are_dropped()
    {
        var plan = CoverTextPlan.From("БЮДЖЕТ 2027", ["12 млн. лв.", "", "   ", "12 МЛН. ЛВ.", null]);

        Assert.Equal(["12 млн. лв."], plan!.KeyPoints);
    }

    [Theory]
    [InlineData("lower-third", CoverTextPlacement.LowerThird)]
    [InlineData("lower third", CoverTextPlacement.LowerThird)]
    [InlineData("LowerRight", CoverTextPlacement.LowerRight)]
    [InlineData("upper-left", CoverTextPlacement.UpperLeft)]
    [InlineData("right_third", CoverTextPlacement.RightThird)]
    public void Placement_accepts_the_kebab_forms_the_model_is_asked_for(string raw, CoverTextPlacement expected)
    {
        Assert.Equal(expected, CoverTextPlan.From("ЗАГЛАВИЕ", null, raw)!.Placement);
    }

    [Fact]
    public void Unknown_placement_and_emphasis_fall_back_to_the_defaults()
    {
        var plan = CoverTextPlan.From("ЗАГЛАВИЕ", null, "middle-of-nowhere", "shouty");

        Assert.Equal(CoverTextPlacement.LowerThird, plan!.Placement);
        Assert.Equal(CoverTextEmphasis.Headline, plan.Emphasis);
    }

    [Fact]
    public void Normalized_is_an_identity_on_an_already_clean_plan()
    {
        var plan = CoverTextPlan.From("ПОЖАР В ПЕТРИЧ", ["3 сгради"], "lower-third", "headline")!;

        Assert.Same(plan, plan.Normalized());
    }

    [Fact]
    public void Normalized_re_applies_the_caps_to_a_hand_built_plan()
    {
        var raw = new CoverTextPlan(
            new string('А', 80), [new string('Б', 40)], CoverTextPlacement.RightThird, CoverTextEmphasis.Number);

        var normalized = raw.Normalized();

        Assert.True(normalized.Headline.Length <= CoverTextPlan.MaxHeadlineChars);
        Assert.All(normalized.KeyPoints, p => Assert.True(p.Length <= CoverTextPlan.MaxKeyPointChars));
        Assert.Equal(CoverTextPlacement.RightThird, normalized.Placement);
        Assert.Equal(CoverTextEmphasis.Number, normalized.Emphasis);
    }
}
