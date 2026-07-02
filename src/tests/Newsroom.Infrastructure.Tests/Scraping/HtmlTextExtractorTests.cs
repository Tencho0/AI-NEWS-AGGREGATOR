using AngleSharp.Html.Parser;
using Newsroom.Infrastructure.Scraping;

namespace Newsroom.Infrastructure.Tests.Scraping;

public class HtmlTextExtractorTests
{
    private static readonly HtmlParser Parser = new();

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Extracts_article_body_and_drops_boilerplate()
    {
        var document = Parser.ParseDocument(LoadFixture("sample-article.html"));

        var text = HtmlTextExtractor.ExtractFromDocument(document, parserHint: null);

        Assert.NotNull(text);
        Assert.Contains("Строителството на нов мост", text);
        Assert.Contains("приоритет номер едно", text);
        // nav, ads, footer must not leak into article text
        Assert.DoesNotContain("Начало", text);
        Assert.DoesNotContain("Реклама", text);
        Assert.DoesNotContain("Всички права запазени", text);
        Assert.DoesNotContain("analytics noise", text);
    }

    [Fact]
    public void ParserHint_selector_overrides_heuristics()
    {
        var document = Parser.ParseDocument(
            "<body><div id='content'><p>Целевият текст от селектора.</p></div>" +
            "<article><p>Друг текст в article елемент, който да не бъде избран.</p></article></body>");

        var text = HtmlTextExtractor.ExtractFromDocument(document, "selector: #content");

        Assert.NotNull(text);
        Assert.Contains("Целевият текст", text);
        Assert.DoesNotContain("Друг текст", text);
    }

    [Fact]
    public void Returns_null_when_no_plausible_article_content()
    {
        var document = Parser.ParseDocument("<body><nav><p>Меню</p></nav><p>Кратко.</p></body>");

        Assert.Null(HtmlTextExtractor.ExtractFromDocument(document, null));
    }

    [Fact]
    public void HtmlToPlainText_strips_markup_and_joins_blocks()
    {
        var text = HtmlTextExtractor.HtmlToPlainText("<p>Първи   абзац.</p><p>Втори <b>абзац</b>.</p>");

        Assert.Equal("Първи абзац.\n\nВтори абзац.", text);
    }
}
