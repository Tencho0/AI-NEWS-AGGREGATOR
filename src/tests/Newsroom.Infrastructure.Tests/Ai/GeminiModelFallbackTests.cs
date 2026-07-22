using Microsoft.Extensions.Logging.Abstractions;

using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiModelFallbackTests
{
    // 2026-07-21 10:00 UTC = 03:00 US-Pacific (PDT, UTC-7). The next Pacific midnight is
    // 2026-07-22 07:00 UTC (or 08:00 UTC under the fixed UTC-8 fallback zone) — the assertions
    // below hold under both, so a missing OS timezone database cannot break the suite.
    private static readonly DateTimeOffset Start = new(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

    private static (GeminiModelFallback Fallback, TestTimeProvider Clock) Create()
    {
        var clock = new TestTimeProvider(Start);
        return (new GeminiModelFallback(clock, NullLogger<GeminiModelFallback>.Instance), clock);
    }

    [Fact]
    public void Inactive_by_default()
    {
        var (fallback, _) = Create();
        Assert.False(fallback.IsActive("gemini-2.5-flash"));
    }

    [Fact]
    public void Activate_makes_only_that_model_active()
    {
        var (fallback, _) = Create();
        fallback.Activate("gemini-3.5-flash");
        Assert.True(fallback.IsActive("gemini-3.5-flash"));
        Assert.False(fallback.IsActive("gemini-2.5-flash")); // per-model, not global
    }

    [Fact]
    public void Stays_active_past_utc_midnight_until_pacific_midnight()
    {
        var (fallback, clock) = Create();
        fallback.Activate("gemini-2.5-flash");

        // 00:30 UTC next day: UTC midnight has passed, Pacific midnight has not — the Gemini
        // quota resets on Pacific time, so the fallback must still be active.
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 0, 30, 0, TimeSpan.Zero);
        Assert.True(fallback.IsActive("gemini-2.5-flash"));

        // Just before Pacific midnight (06:59 UTC under PDT; the UTC-8 fallback resets later).
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 6, 59, 0, TimeSpan.Zero);
        Assert.True(fallback.IsActive("gemini-2.5-flash"));
    }

    [Fact]
    public void Expires_after_pacific_midnight()
    {
        var (fallback, clock) = Create();
        fallback.Activate("gemini-2.5-flash");

        // 08:01 UTC is past Pacific midnight under both PDT (07:00) and fixed UTC-8 (08:00).
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 8, 1, 0, TimeSpan.Zero);
        Assert.False(fallback.IsActive("gemini-2.5-flash"));
    }

    [Fact]
    public void Can_reactivate_after_expiry()
    {
        var (fallback, clock) = Create();
        fallback.Activate("gemini-2.5-flash");
        clock.UtcNow = new DateTimeOffset(2026, 7, 22, 8, 1, 0, TimeSpan.Zero);
        Assert.False(fallback.IsActive("gemini-2.5-flash"));

        fallback.Activate("gemini-2.5-flash"); // quota still exhausted after a re-probe
        Assert.True(fallback.IsActive("gemini-2.5-flash"));
    }
}
