using System.Globalization;

using Newsroom.Core.Operations;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Data retention (docs/04-technical-spec.md, docs/06-security.md: ExtractedText is an
/// internal working copy, pruned per policy). Once per day — same cadence pattern as the
/// digest, its own nw_Config date key ('Retention:LastRunDate') — it NULLs ExtractedText on
/// articles first seen more than Retention:ExtractedTextDays ago and deletes nw_Log rows
/// older than Retention:LogDays. Both are idempotent set-based statements, so a crash between
/// the steps and the date write just repeats harmless work next minute.
/// </summary>
public sealed class RetentionJob(
    IOperationsRepository operations,
    IConfiguration configuration,
    ILogger<RetentionJob> logger) : BackgroundService
{
    public const string LastRunDateKey = "Retention:LastRunDate";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);

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
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (await operations.GetConfigValueAsync(LastRunDateKey, ct) == today)
                return; // already ran today

            var nowUtc = DateTime.UtcNow;

            var textDays = configuration.GetValue("Retention:ExtractedTextDays", 90);
            var pruned = await operations.ClearExpiredExtractedTextAsync(
                nowUtc.AddDays(-textDays), ct);
            if (pruned > 0)
                logger.LogInformation(
                    "Retention: cleared ExtractedText on {Count} article(s) older than {Days} days",
                    pruned, textDays);

            var logDays = configuration.GetValue("Retention:LogDays", 90);
            var deleted = await operations.DeleteExpiredLogsAsync(nowUtc.AddDays(-logDays), ct);
            if (deleted > 0)
                logger.LogInformation(
                    "Retention: deleted {Count} nw_Log row(s) older than {Days} days",
                    deleted, logDays);

            await operations.SetConfigValueAsync(LastRunDateKey, today, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Retention cycle failed"); // retried next minute
        }
    }
}
