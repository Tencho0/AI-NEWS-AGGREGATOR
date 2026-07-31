using Newsroom.Core.Operations;

namespace Newsroom.Core.Tests.Operations;

public class DigestPolicyTests
{
    [Fact]
    public void Reports_the_day_before_the_send()
    {
        // 09:00 Bulgarian summer time = 06:00 UTC — the moment the job actually fires.
        var nowUtc = new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            DigestPolicy.DayToReport(nowUtc));
    }

    [Fact]
    public void Reported_day_is_a_midnight_boundary_whatever_the_send_time()
    {
        var day = DigestPolicy.DayToReport(new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc));

        Assert.Equal(TimeSpan.Zero, day.TimeOfDay);
        Assert.Equal(new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc), day);
    }

    [Fact]
    public void Reported_day_never_runs_past_the_end_of_the_window()
    {
        var nowUtc = new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc);

        var day = DigestPolicy.DayToReport(nowUtc);

        // The whole point of the rule: the window is closed before it is reported, so no
        // activity can still land inside it after the digest goes out.
        Assert.True(day.AddDays(1) <= nowUtc);
    }

    [Fact]
    public void Month_and_year_boundaries_roll_back_correctly()
    {
        Assert.Equal(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            DigestPolicy.DayToReport(new DateTime(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            DigestPolicy.DayToReport(new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Reported_day_keeps_the_UTC_kind_it_was_given()
    {
        var day = DigestPolicy.DayToReport(new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateTimeKind.Utc, day.Kind);
    }
}
