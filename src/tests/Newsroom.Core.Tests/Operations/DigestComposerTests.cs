using Newsroom.Core.Operations;

namespace Newsroom.Core.Tests.Operations;

public class DigestComposerTests
{
    private static readonly DateTime Day = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);

    private static DigestStats FullDay() => new()
    {
        DayUtc = Day,
        ArticlesScraped = 42,
        ArticlesPerSource = [("Дневник", 30), ("БНТ", 12)],
        ArticlesAnalysed = 35,
        ArticlesIgnored = 5,
        TopicsCreated = 3,
        HotTopics = 2,
        DraftsCreatedByStatus = [("PendingReview", 2), ("Published", 1)],
        ReviewActions = [("Approved", 2), ("Rejected", 1)],
        PublishSucceeded = 3,
        PublishFailed = 1,
        AiRequests = 120,
        AiTokensIn = 400_000,
        AiTokensOut = 50_000,
        AiCost = 0.1234m,
        SourcesEnabled = 12,
        SourcesDisabled = 1,
    };

    [Fact]
    public void Composes_every_section_with_the_day_in_the_header()
    {
        var text = DigestComposer.Compose(FullDay());

        Assert.Contains("Дневен отчет — 02.07.2026", text);
        Assert.Contains("Статии: 42 (анализирани 35 · игнорирани 5)", text);
        Assert.Contains("• Дневник: 30", text);
        Assert.Contains("• БНТ: 12", text);
        Assert.Contains("Теми: 3 нови · 2 горещи в момента", text);
        Assert.Contains("Чернови днес: PendingReview 2 · Published 1", text);
        Assert.Contains("Редакторски действия: Approved 2 · Rejected 1", text);
        Assert.Contains("Публикации: 3 успешни · 1 неуспешни", text);
        Assert.Contains("AI: 120 заявки · 400000/50000 токена · $0.1234", text);
        Assert.Contains("Източници: 12 активни · 1 изключени", text);
    }

    [Fact]
    public void Escapes_html_in_database_strings()
    {
        var stats = FullDay() with
        {
            ArticlesPerSource = [("Би<Ти>Ви & Ко", 7)],
            DraftsCreatedByStatus = [("<Status>", 1)],
            ReviewActions = [("A&B", 1)],
        };

        var text = DigestComposer.Compose(stats);

        Assert.Contains("Би&lt;Ти&gt;Ви &amp; Ко", text);
        Assert.Contains("&lt;Status&gt; 1", text);
        Assert.Contains("A&amp;B 1", text);
        Assert.DoesNotContain("<Status>", text);
        Assert.DoesNotContain("Би<Ти>Ви", text);
    }

    [Fact]
    public void Quiet_day_reads_as_no_activity_instead_of_crashing()
    {
        var text = DigestComposer.Compose(new DigestStats { DayUtc = Day });

        Assert.Contains("Статии: 0\n", text);
        Assert.DoesNotContain("анализирани", text); // no breakdown when nothing was scraped
        Assert.Contains("Чернови днес: няма", text);
        Assert.Contains("Редакторски действия: няма", text);
        Assert.Contains("AI: 0 заявки", text);
        Assert.Contains("$0", text);
    }

    [Fact]
    public void Cost_uses_invariant_decimal_formatting()
    {
        var text = DigestComposer.Compose(FullDay() with { AiCost = 1.5m });

        Assert.Contains("$1.5", text);
        Assert.DoesNotContain("$1,5", text);
    }
}
