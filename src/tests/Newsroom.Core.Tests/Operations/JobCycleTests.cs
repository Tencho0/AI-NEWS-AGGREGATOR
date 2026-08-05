using Newsroom.Core.Operations;

namespace Newsroom.Core.Tests.Operations;

/// <summary>
/// Regression cover for the 2026-08-04 outage: DraftJob and TrendJob went silent mid-cycle and
/// never ran again, while the host and every other job stayed healthy. The cause was the job
/// loop's <c>catch (OperationCanceledException)</c> being read as "the host is shutting down".
/// An HttpClient timeout surfaces as TaskCanceledException — an OperationCanceledException — so
/// one stalled Gemini call retired the job for the life of the process, with nothing logged.
/// </summary>
public class JobCycleTests
{
    /// <summary>Short enough to keep the suite fast, long enough not to starve the scheduler.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);

    /// <summary>Fails the test rather than hanging the suite if a loop refuses to exit.</summary>
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Http_timeout_does_not_retire_the_job()
    {
        using var deadline = Deadline();
        var cycles = 0;
        var failures = new List<Exception>();

        await JobCycle.RunAsync(
            Tick,
            _ =>
            {
                cycles++;
                // Exactly what HttpClient throws when its timeout elapses.
                if (cycles == 1)
                    throw new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
                if (cycles >= 4)
                    deadline.Cancel(); // enough proof the loop survived
                return Task.CompletedTask;
            },
            failures.Add,
            deadline.Token);

        Assert.True(cycles >= 4, $"loop retired after {cycles} cycle(s); it must keep running");
        Assert.Single(failures);
        Assert.IsType<TaskCanceledException>(failures[0]);
    }

    [Fact]
    public async Task Unexpected_exception_does_not_retire_the_job()
    {
        using var deadline = Deadline();
        var cycles = 0;
        var failures = new List<Exception>();

        await JobCycle.RunAsync(
            Tick,
            _ =>
            {
                cycles++;
                if (cycles == 1)
                    throw new InvalidOperationException("config is malformed");
                if (cycles >= 4)
                    deadline.Cancel();
                return Task.CompletedTask;
            },
            failures.Add,
            deadline.Token);

        Assert.True(cycles >= 4, $"loop retired after {cycles} cycle(s); it must keep running");
        Assert.IsType<InvalidOperationException>(Assert.Single(failures));
    }

    [Fact]
    public async Task Host_shutdown_exits_the_loop_quietly()
    {
        using var deadline = Deadline();
        using var stopping = new CancellationTokenSource();
        var failures = new List<Exception>();

        await JobCycle.RunAsync(
            Tick,
            _ =>
            {
                stopping.Cancel();
                stopping.Token.ThrowIfCancellationRequested(); // as an awaited call would
                return Task.CompletedTask;
            },
            failures.Add,
            stopping.Token);

        // Shutdown is not a failure: nothing to report, and no exception escapes.
        Assert.Empty(failures);
        Assert.False(deadline.IsCancellationRequested, "the loop should have exited well before the deadline");
    }

    [Fact]
    public async Task Shutdown_while_a_cycle_is_in_flight_is_not_reported_as_a_failure()
    {
        // The race the guard has to get right: the host stops *during* a cycle, so the cycle's
        // own cancellation is genuine shutdown and must not be logged as a job failure.
        using var stopping = new CancellationTokenSource();
        var failures = new List<Exception>();

        await JobCycle.RunAsync(
            Tick,
            async ct =>
            {
                stopping.Cancel();
                await Task.Delay(TimeSpan.FromMinutes(1), ct); // observes the cancellation
            },
            failures.Add,
            stopping.Token);

        Assert.Empty(failures);
    }
}
