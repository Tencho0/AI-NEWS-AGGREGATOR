using Newsroom.Core.Review;

namespace Newsroom.Core.Tests.Review;

public class ReviewMessageRendererTests
{
    private static DraftReviewView View(
        string headline = "МОЩЕН ТРУС РАЗТЪРСИ ЮГОЗАПАДА",
        string? subtitle = "Усетен и в Благоевград",
        string body = "Земетресение разтърси региона, съобщи БТА.",
        int sourceCount = 5,
        IReadOnlyList<(string Name, string Url)>? sources = null,
        IReadOnlyList<string>? flaggedClaims = null,
        string? model = "gemini-2.5-flash") => new(
        DraftId: 42,
        Version: 2,
        TopicLabel: "Земетресение в Югозапада",
        TopicScore: 8.2,
        SourceCount: sourceCount,
        Headline: headline,
        Subtitle: subtitle,
        BodyMarkdown: body,
        Category: "Общество",
        Region: "Благоевград",
        Tags: ["земетресение", "Благоевград"],
        Sources: sources ?? [("БТА", "https://example.com/1"), ("Дневник", "https://example.com/2")],
        FlaggedClaims: flaggedClaims ?? [],
        Confidence: 0.85,
        Cost: 0.0012m,
        Model: model,
        ImageCount: 2,
        TelegramMessageId: null);

    [Fact]
    public void Renders_the_documented_layout()
    {
        var html = ReviewMessageRenderer.RenderHtml(View());

        Assert.Contains("🔥 Земетресение в Югозапада (score 8.2, 5 източника)", html);
        Assert.Contains("━━━", html);
        Assert.Contains("<b>МОЩЕН ТРУС РАЗТЪРСИ ЮГОЗАПАДА</b>", html);
        Assert.Contains("<i>Усетен и в Благоевград</i>", html);
        Assert.Contains("Земетресение разтърси региона, съобщи БТА.", html);
        Assert.Contains("📎 Категория: Общество · Регион: Благоевград · Тагове: земетресение, Благоевград", html);
        Assert.Contains("💰 $0.0012 · v2 · модел gemini-2.5-flash", html);
    }

    [Fact]
    public void Hostile_content_is_html_escaped_everywhere()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(
            headline: "<b>&",
            subtitle: "1 < 2 & 3 > 2",
            body: "Тяло с <script>alert()</script> & още.",
            sources: [("<Медия> & Ко", "https://example.com/a?b=1&c=<2>")],
            flaggedClaims: ["Претенция с <таг> & амперсанд."]));

        Assert.Contains("<b>&lt;b&gt;&amp;</b>", html);
        Assert.Contains("<i>1 &lt; 2 &amp; 3 &gt; 2</i>", html);
        Assert.Contains("Тяло с &lt;script&gt;alert()&lt;/script&gt; &amp; още.", html);
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=&lt;2&gt;\">&lt;Медия&gt; &amp; Ко</a>", html);
        Assert.Contains("• Претенция с &lt;таг&gt; &amp; амперсанд.", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Long_body_is_truncated_on_a_word_boundary_with_ellipsis()
    {
        // 10-char words: char 1500 falls mid-word, so the cut must retreat to a full word.
        var body = string.Join(" ", Enumerable.Repeat("abcdefghij", 200));

        var html = ReviewMessageRenderer.RenderHtml(View(body: body));

        Assert.Contains("abcdefghij …", html);
        var rendered = html.Split('\n').Single(line => line.EndsWith(" …", StringComparison.Ordinal));
        Assert.True(rendered.Length <= ReviewMessageRenderer.MaxBodyChars + 2);
        // Every rendered word is complete — no fragment like "abcde …" survived the cut.
        Assert.All(rendered.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            word => Assert.True(word is "abcdefghij" or "…", $"unexpected fragment '{word}'"));
    }

    [Fact]
    public void Short_body_is_kept_untruncated()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(body: "Кратко тяло."));

        Assert.Contains("Кратко тяло.", html);
        Assert.DoesNotContain("…", html);
    }

    [Fact]
    public void Flagged_claims_section_appears_only_when_there_are_claims()
    {
        var without = ReviewMessageRenderer.RenderHtml(View());
        var with = ReviewMessageRenderer.RenderHtml(View(
            flaggedClaims: ["Магнитудът идва само от един източник.", "Числото 4.5 е непроверено."]));

        Assert.DoesNotContain("⚠️", without);
        Assert.Contains("⚠️ За проверка:", with);
        Assert.Contains("• Магнитудът идва само от един източник.", with);
        Assert.Contains("• Числото 4.5 е непроверено.", with);
    }

    [Fact]
    public void Sources_render_as_numbered_links()
    {
        var html = ReviewMessageRenderer.RenderHtml(View());

        Assert.Contains("🔗 Източници:", html);
        Assert.Contains("1. <a href=\"https://example.com/1\">БТА</a>", html);
        Assert.Contains("2. <a href=\"https://example.com/2\">Дневник</a>", html);
    }

    [Fact]
    public void Single_source_uses_singular_wording()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(sourceCount: 1));

        Assert.Contains("(score 8.2, 1 източник)", html);
    }

    [Fact]
    public void Missing_model_and_subtitle_degrade_gracefully()
    {
        var html = ReviewMessageRenderer.RenderHtml(View(subtitle: null, model: null));

        Assert.DoesNotContain("<i>", html);
        Assert.Contains("модел —", html);
    }

    [Fact]
    public void Resolved_suffix_is_escaped_and_separated()
    {
        var suffix = ReviewMessageRenderer.RenderResolvedSuffix("✅ Одобрено от <Иван>");

        Assert.Equal("\n\n✅ Одобрено от &lt;Иван&gt;", suffix);
    }
}
