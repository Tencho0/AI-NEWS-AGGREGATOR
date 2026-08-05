namespace Newsroom.Core.Operations;

/// <summary>
/// The periodic loop every worker job runs on: run a cycle, wait for the next tick, repeat until
/// the host stops. Shared so the cancellation rules below hold identically for all of them.
///
/// <para>The rule that matters: <b>only the host's own token ends the loop.</b> Every other
/// failure — including a cancellation that is not shutdown — is reported and the job takes the
/// next tick. This is not defensive padding, it is the fix for the 2026-08-04 outage: each job
/// ended in a bare <c>catch (OperationCanceledException)</c> commented "graceful shutdown", and
/// an <see cref="HttpClient"/> timeout throws <see cref="TaskCanceledException"/>, which derives
/// from <see cref="OperationCanceledException"/>. One stalled Gemini call therefore looked like
/// shutdown, <c>ExecuteAsync</c> returned normally, and because a completed
/// <c>BackgroundService</c> neither faults the host nor logs anything, DraftJob and TrendJob were
/// silently retired for the life of the process while every other job kept running. Draft was
/// dead 21 hours before anyone noticed.</para>
/// </summary>
public static class JobCycle
{
    /// <summary>
    /// Runs <paramref name="runCycle"/> every <paramref name="interval"/> until
    /// <paramref name="stoppingToken"/> is cancelled. A cycle that throws is reported through
    /// <paramref name="onCycleFailed"/> and the loop continues; the callback must not throw.
    /// Cycles do not overlap — a slow cycle delays the next tick rather than running alongside it.
    /// </summary>
    public static async Task RunAsync(
        TimeSpan interval,
        Func<CancellationToken, Task> runCycle,
        Action<Exception> onCycleFailed,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                try
                {
                    await runCycle(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The host really is stopping: end the loop through the handler below.
                    // The filter is evaluated before the stack unwinds, so this asks "was the
                    // host stopping at the moment of the throw", which is the question that
                    // distinguishes shutdown from a client-side timeout.
                    throw;
                }
                catch (Exception ex)
                {
                    // Anything else — an HttpClient timeout, a malformed config value, a bug —
                    // costs this cycle only. Reporting it is what makes the failure visible;
                    // the job stays alive and retries on the next tick.
                    onCycleFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }
}
