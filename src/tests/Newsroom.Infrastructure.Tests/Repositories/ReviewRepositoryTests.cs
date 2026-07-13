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
    public void FormatHealthSummary_reports_age_and_flags_stale_jobs()
    {
        var now = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var text = ReviewRepository.FormatHealthSummary(
        [
            ("Scrape", now.AddMinutes(-2)),   // fresh
            ("Draft", now.AddMinutes(-40)),   // stale (> 15)
            ("Publish", null),                // never beaten
        ], now, staleMinutes: 15);

        Assert.Contains("Scrape: преди 2 мин", text);
        Assert.Contains("Draft: преди 40 мин ⚠️ закъснява", text);
        Assert.Contains("Publish: няма", text);
    }
}
