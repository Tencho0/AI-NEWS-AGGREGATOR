namespace Newsroom.Core.Operations;

/// <summary>
/// Pure staleness decision for the watchdog (docs/07-operations.md: "job hasn't completed a
/// cycle within 3× its interval → ⚠️"). Kept free of I/O so the whole matrix is unit-testable;
/// WatchdogJob owns the schedule, the expectations table and the alert rate limit.
/// </summary>
public static class WatchdogPolicy
{
    /// <summary>
    /// True when the job should be alerted on. The first <paramref name="allowedStaleness"/>
    /// after <paramref name="processStartUtc"/> is a grace period for every job: a fresh
    /// process has either no heartbeat key yet (first ever run) or a stale one written before
    /// the restart — both mean "the job has not had a fair chance", not "the job is dead".
    /// After the grace period a missing key alerts, and an existing beat alerts once it is
    /// older than the allowance.
    /// </summary>
    public static bool ShouldAlert(
        DateTime? lastBeatUtc, TimeSpan allowedStaleness, DateTime nowUtc, DateTime processStartUtc)
    {
        if (nowUtc - processStartUtc <= allowedStaleness)
            return false; // startup grace: jobs have not had a full allowance to beat yet

        if (lastBeatUtc is null)
            return true; // never beat although the process has been up long enough

        return nowUtc - lastBeatUtc.Value > allowedStaleness;
    }
}
