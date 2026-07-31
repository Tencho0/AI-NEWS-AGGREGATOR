using Newsroom.Core.Images;

namespace Newsroom.Core.Tests.Images;

public class PublicFigureDirectoryTests
{
    private static PublicFigureDirectory Configured() => new(
    [
        new PublicFigure("Иван Иванов", "кмет на Благоевград", "ivanov.jpg", ["кметът Иванов"]),
        new PublicFigure("Мария Петрова", "областен управител", "petrova.jpg", []),
    ]);

    [Fact]
    public void Finds_a_figure_named_in_the_sources()
    {
        var mentioned = Configured().Mentioned("Днес Иван Иванов подписа договора.");

        Assert.Equal("Иван Иванов", Assert.Single(mentioned).Name);
    }

    [Fact]
    public void Finds_a_figure_by_alias()
    {
        var mentioned = Configured().Mentioned("Според кметът Иванов ремонтът започва идната седмица.");

        Assert.Equal("Иван Иванов", Assert.Single(mentioned).Name);
    }

    [Fact]
    public void Returns_every_mentioned_figure_in_configured_order()
    {
        var mentioned = Configured().Mentioned("Мария Петрова и Иван Иванов откриха обекта.");

        Assert.Equal(["Иван Иванов", "Мария Петрова"], mentioned.Select(f => f.Name));
    }

    [Fact]
    public void A_name_inside_a_longer_word_is_not_a_mention()
    {
        // „Иванова" is a different person; substring matching would wrongly fire here.
        var mentioned = Configured().Mentioned("Изявление на Иванова от общината.");

        Assert.Empty(mentioned);
    }

    [Fact]
    public void Unmentioned_figures_and_empty_text_yield_nothing()
    {
        Assert.Empty(Configured().Mentioned("Общината обяви обществена поръчка."));
        Assert.Empty(Configured().Mentioned(""));
        Assert.Empty(Configured().Mentioned(null));
        Assert.Empty(PublicFigureDirectory.Empty.Mentioned("Иван Иванов"));
    }

    [Fact]
    public void Find_resolves_a_configured_name_case_insensitively()
    {
        Assert.Equal("Иван Иванов", Configured().Find("иван иванов")!.Name);
        Assert.Equal("Иван Иванов", Configured().Find("  Иван Иванов  ")!.Name);
        Assert.Equal("Иван Иванов", Configured().Find("кметът Иванов")!.Name);
    }

    [Fact]
    public void Find_rejects_a_name_the_configuration_does_not_know()
    {
        // The guard against a hallucinated name reaching the image prompt.
        Assert.Null(Configured().Find("Георги Георгиев"));
        Assert.Null(Configured().Find(null));
        Assert.Null(Configured().Find(""));
    }

    [Fact]
    public void Sensitive_categories_block_a_likeness_unless_explicitly_overridden()
    {
        Assert.False(CoverPersonPolicy.MayDepict("Криминално", allowInSensitiveCategories: false));
        Assert.True(CoverPersonPolicy.MayDepict("Криминално", allowInSensitiveCategories: true));
        Assert.True(CoverPersonPolicy.MayDepict("Политика", allowInSensitiveCategories: false));
        Assert.True(CoverPersonPolicy.MayDepict(null, allowInSensitiveCategories: false));
    }
}
