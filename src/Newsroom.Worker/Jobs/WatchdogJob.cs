using Newsroom.Core.Operations;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Publishing;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// The watchdog behind the ops table in docs/07-operations.md: every Watchdog:CheckSeconds it
/// compares each job's 'Heartbeat:{job}' beat (written by the jobs via <see cref="IJobHeartbeat"/>)
/// against 3× that job's own configured interval — the same config keys the jobs read, so
/// tuning an interval tunes its allowance too. Staleness decisions live in
/// <see cref="WatchdogPolicy"/> (startup grace included); alerts go to the review chat, rate
/// limited to one per job per hour, and are best-effort like every Telegram notification.
/// TelegramJob and PublishJob are only watched when their options say they actually run.
/// </summary>
public sealed class WatchdogJob(
    IOperationsRepository operations,
    Lazy<ITelegramGateway> gateway,
    IConfiguration configuration,
    ILogger<WatchdogJob> logger) : BackgroundService
{
    private static readonly TimeSpan AlertInterval = TimeSpan.FromHours(1);

    private readonly DateTime processStartUtc = DateTime.UtcNow;
    private readonly Dictionary<string, DateTime> lastAlertUtc = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(configuration.GetValue("Watchdog:CheckSeconds", 300));
        using var timer = new PeriodicTimer(checkInterval);

        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, DateTime> beats;
        try
        {
            beats = await operations.GetHeartbeatsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The DB being unreachable shows up as stale beats soon enough; don't crash the dog.
            logger.LogError(ex, "Watchdog could not read the job heartbeats");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var (jobName, allowedStaleness) in BuildExpectations())
        {
            ct.ThrowIfCancellationRequested();

            DateTime? lastBeatUtc = beats.TryGetValue(jobName, out var beat) ? beat : null;
            if (!WatchdogPolicy.ShouldAlert(lastBeatUtc, allowedStaleness, nowUtc, processStartUtc))
                continue;

            var age = nowUtc - (lastBeatUtc ?? processStartUtc);
            logger.LogWarning(
                "Job {Job} has not completed a cycle for {AgeMinutes} min (allowed {AllowedMinutes} min)",
                jobName, (int)age.TotalMinutes, (int)allowedStaleness.TotalMinutes);
            await TryAlertAsync(jobName, age, nowUtc, ct);
        }
    }

    /// <summary>
    /// The expected-jobs table: allowance = 3× the interval each job configures itself with
    /// (docs/07-operations.md). TelegramJob has no interval — its loop is paced by the long
    /// poll, so 3× the poll timeout plus a minute of processing slack. Telegram and Publish
    /// only appear when configured; unconfigured jobs log one warning and stay dormant, which
    /// must not page anyone.
    /// </summary>
    private List<(string JobName, TimeSpan AllowedStaleness)> BuildExpectations()
    {
        var expectations = new List<(string, TimeSpan)>
        {
            (JobNames.Scrape, Allowance("Scrape:CheckSeconds", 60)),
            (JobNames.Analyse, Allowance("Ai:Stages:Analyse:CheckSeconds", 120)),
            (JobNames.Trend, Allowance("Ai:Stages:Cluster:CheckSeconds", 300)),
            (JobNames.Draft, Allowance("Ai:Stages:Draft:CheckSeconds", 300)),
        };

        var telegram = TelegramOptions.From(configuration);
        if (telegram.IsConfigured)
            expectations.Add((JobNames.Telegram,
                TimeSpan.FromSeconds(3 * telegram.PollTimeoutSeconds + 60)));

        var umbraco = UmbracoOptions.From(configuration);
        if (umbraco.IsConfigured)
            expectations.Add((JobNames.Publish, TimeSpan.FromSeconds(3 * umbraco.CheckSeconds)));

        return expectations;
    }

    private TimeSpan Allowance(string intervalKey, int defaultSeconds) =>
        TimeSpan.FromSeconds(3 * configuration.GetValue(intervalKey, defaultSeconds));

    /// <summary>One alert per job per hour (in-memory: a restart resets the limiter, but the
    /// startup grace in <see cref="WatchdogPolicy"/> keeps that quiet). Skipped when Telegram
    /// is not configured — the warning log above is then the only signal, per docs/07.</summary>
    private async Task TryAlertAsync(string jobName, TimeSpan age, DateTime nowUtc, CancellationToken ct)
    {
        if (lastAlertUtc.TryGetValue(jobName, out var last) && nowUtc - last < AlertInterval)
            return;
        lastAlertUtc[jobName] = nowUtc; // also rate-limits failed sends — no hammering Telegram

        var telegram = TelegramOptions.From(configuration);
        if (!telegram.IsConfigured)
            return;

        try
        {
            await gateway.Value.SendHtmlAsync(
                telegram.ReviewChatId,
                ReviewMessageRenderer.Escape(
                    $"⚠️ Задачата {jobName} не е отчела активност от {(int)age.TotalMinutes} мин"),
                withReviewButtons: false, draftIdForButtons: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the watchdog alert for job {Job}", jobName);
        }
    }
}
