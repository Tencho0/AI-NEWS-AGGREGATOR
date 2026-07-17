using System.Globalization;

using Newsroom.Core.Operations;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Sends the daily ℹ️ digest (docs/07-operations.md) to the review chat once per day at
/// Digest:LocalTime (default 09:00, VPS-local). The loop checks every minute whether the
/// target minute has passed and today has not been sent yet; the last-sent date persists in
/// nw_Config ('Digest:LastSentDate'), so restarts never double-send and a worker that was down
/// at 09:00 sends the digest as soon as it is back. Content is composed by the pure
/// <see cref="DigestComposer"/> over one repository aggregate covering today (UTC). Without
/// Telegram configuration the day is marked handled silently — same dormancy rule as the
/// other Telegram-facing jobs.
/// </summary>
public sealed class DailyDigestJob(
    IOperationsRepository operations,
    Lazy<ITelegramGateway> gateway,
    IConfiguration configuration,
    ILogger<DailyDigestJob> logger) : BackgroundService
{
    public const string LastSentDateKey = "Digest:LastSentDate";

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
            var localNow = DateTime.Now;
            if (localNow.TimeOfDay < TargetLocalTime())
                return; // today's send minute has not arrived yet

            var today = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (await operations.GetConfigValueAsync(LastSentDateKey, ct) == today)
                return; // already sent today (possibly by a previous process)

            var telegram = TelegramOptions.From(configuration);
            if (!telegram.IsConfigured)
            {
                // No review chat to send to: mark the day handled so this stays a cheap check.
                await operations.SetConfigValueAsync(LastSentDateKey, today, ct);
                logger.LogDebug("Daily digest skipped: Telegram is not configured");
                return;
            }

            var stats = await operations.GetDigestStatsAsync(DateTime.UtcNow.Date, ct);
            await gateway.Value.SendHtmlAsync(
                telegram.ReviewChatId, DigestComposer.Compose(stats),
                withReviewButtons: false, draftIdForButtons: null, scheduleButtonLabel: null, ct);
            // Marked sent only after the send succeeded — a failed send retries next minute.
            await operations.SetConfigValueAsync(LastSentDateKey, today, ct);
            logger.LogInformation("ℹ️ Daily digest sent for {Date}", today);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Daily digest cycle failed"); // retried next minute
        }
    }

    /// <summary>Digest:LocalTime as a time of day; malformed values fall back to 09:00 —
    /// a broken config line must not silence the digest forever.</summary>
    private TimeSpan TargetLocalTime()
    {
        var configured = configuration.GetValue("Digest:LocalTime", "09:00");
        return TimeOnly.TryParseExact(configured, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var target)
            ? target.ToTimeSpan()
            : new TimeSpan(9, 0, 0);
    }
}
