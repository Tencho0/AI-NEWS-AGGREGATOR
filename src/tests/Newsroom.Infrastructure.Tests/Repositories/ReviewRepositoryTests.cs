using Newsroom.Infrastructure.Repositories;

namespace Newsroom.Infrastructure.Tests.Repositories;

public class ReviewRepositoryTests
{
    [Fact]
    public void FormatQuotaSummary_lists_each_stage_used_over_cap()
    {
        var text = ReviewRepository.FormatQuotaSummary(
        [
            ("Draft", 6, 20),
            ("SelfCheck", 3, 20),
        ]);

        Assert.Contains("Draft 6/20", text);
        Assert.Contains("SelfCheck 3/20", text);
        Assert.DoesNotContain("⚠️", text);
    }

    [Fact]
    public void FormatQuotaSummary_flags_a_stage_at_or_over_its_cap()
    {
        var text = ReviewRepository.FormatQuotaSummary([("Draft", 20, 20)]);

        Assert.Contains("Draft 20/20 ⚠️", text);
    }

    [Fact]
    public void FormatQuotaSummary_handles_no_stages()
    {
        Assert.Equal("Няма конфигурирани AI етапи.", ReviewRepository.FormatQuotaSummary([]));
    }

    [Fact]
    public void FormatHealthSummary_reports_age_and_flags_jobs_past_their_allowance()
    {
        var now = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var text = ReviewRepository.FormatHealthSummary(
        [
            ("Scrape", now.AddMinutes(-2), TimeSpan.FromMinutes(3)),    // fresh (2 < 3)
            ("Analyse", now.AddMinutes(-6), TimeSpan.FromMinutes(6)),   // exactly at allowance → not stale
            ("Draft", now.AddMinutes(-40), TimeSpan.FromMinutes(15)),   // stale (40 > 15)
            ("Publish", null, TimeSpan.FromMinutes(3)),                 // never beaten
        ], now);

        Assert.Contains("Scrape: преди 2 мин", text);
        Assert.DoesNotContain("преди 2 мин ⚠️", text);   // fresh job not flagged
        Assert.Contains("Analyse: преди 6 мин", text);
        Assert.DoesNotContain("преди 6 мин ⚠️", text);   // boundary: age == allowance is not stale
        Assert.Contains("Draft: преди 40 мин ⚠️ закъснява", text);
        Assert.Contains("Publish: няма", text);
    }
}
