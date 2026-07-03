using Newsroom.Core.Operations;

namespace Newsroom.Core.Tests.Operations;

public class WatchdogPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Allowed = TimeSpan.FromMinutes(15);

    /// <summary>Process running long enough that the startup grace no longer applies.</summary>
    private static readonly DateTime OldStart = Now.AddHours(-6);

    [Fact]
    public void Fresh_beat_does_not_alert()
    {
        Assert.False(WatchdogPolicy.ShouldAlert(Now.AddMinutes(-1), Allowed, Now, OldStart));
    }

    [Fact]
    public void Beat_exactly_at_the_allowance_does_not_alert()
    {
        Assert.False(WatchdogPolicy.ShouldAlert(Now - Allowed, Allowed, Now, OldStart));
    }

    [Fact]
    public void Beat_older_than_the_allowance_alerts()
    {
        Assert.True(WatchdogPolicy.ShouldAlert(
            Now - Allowed - TimeSpan.FromSeconds(1), Allowed, Now, OldStart));
    }

    [Fact]
    public void Missing_beat_alerts_once_the_process_outlives_the_allowance()
    {
        Assert.True(WatchdogPolicy.ShouldAlert(
            null, Allowed, Now, Now - Allowed - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Missing_beat_does_not_alert_while_the_process_is_younger_than_the_allowance()
    {
        Assert.False(WatchdogPolicy.ShouldAlert(null, Allowed, Now, Now.AddMinutes(-5)));
    }

    [Fact]
    public void Missing_beat_does_not_alert_at_exactly_the_allowance_of_uptime()
    {
        Assert.False(WatchdogPolicy.ShouldAlert(null, Allowed, Now, Now - Allowed));
    }

    [Fact]
    public void Stale_beat_from_before_a_restart_gets_the_startup_grace()
    {
        // The worker restarted 5 minutes ago; the key still holds a beat from 3 hours ago.
        // The job is running its first cycles right now — no alert until the grace passes.
        Assert.False(WatchdogPolicy.ShouldAlert(
            Now.AddHours(-3), Allowed, Now, Now.AddMinutes(-5)));
    }

    [Fact]
    public void Stale_beat_alerts_after_the_startup_grace_has_passed()
    {
        Assert.True(WatchdogPolicy.ShouldAlert(
            Now.AddHours(-3), Allowed, Now, Now.AddMinutes(-16)));
    }
}
