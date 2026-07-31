using System.Globalization;

using Newsroom.Core.Images;
using Newsroom.Core.Operations;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Data retention (docs/04-technical-spec.md, docs/06-security.md: ExtractedText is an
/// internal working copy, pruned per policy). Once per day — same cadence pattern as the
/// digest, its own nw_Config date key ('Retention:LastRunDate') — it NULLs ExtractedText on
/// articles first seen more than Retention:ExtractedTextDays ago, deletes nw_Log rows
/// older than Retention:LogDays, and (ADR-0013) deletes the local image files that are no longer
/// useful. All steps are idempotent, so a crash between them and the date write just repeats
/// harmless work next minute.
/// </summary>
public sealed class RetentionJob(
    IOperationsRepository operations,
    ImageStorage storage,
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

            await PruneImageFilesAsync(nowUtc, ct);

            await operations.SetConfigValueAsync(LastRunDateKey, today, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Retention cycle failed"); // retried next minute
        }
    }

    /// <summary>
    /// Deletes the local files behind resolved drafts' images (ADR-0013). The repository query is
    /// the first guard — it only returns rows whose draft has finished its life. This method adds
    /// the second: a file is deleted only when it resolves *inside* the generated-images or
    /// editor-uploads area, so a corrupted or hostile Url can never point the deleter at the
    /// public-figure reference photos, the logo asset, or anything outside the storage root.
    /// A row is stamped as pruned even when its file is already gone, so it stops coming back.
    /// </summary>
    private async Task PruneImageFilesAsync(DateTime nowUtc, CancellationToken ct)
    {
        var generatedDays = configuration.GetValue("Retention:GeneratedImageDays", 14);
        var uploadDays = configuration.GetValue("Retention:EditorUploadDays", 30);
        var publishedDays = configuration.GetValue("Retention:PublishedImageDays", 30);
        var batch = configuration.GetValue("Retention:ImageBatch", 500);

        var candidates = await operations.GetPrunableImagesAsync(
            nowUtc.AddDays(-generatedDays),
            nowUtc.AddDays(-uploadDays),
            nowUtc.AddDays(-publishedDays),
            batch,
            ct);
        if (candidates.Count == 0)
            return;

        var prunedIds = new List<long>(candidates.Count);
        var deletedFiles = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!storage.TryResolve(candidate.StorageKey, out var path))
            {
                // Unresolvable (remote, traversal, orphaned legacy path) — nothing to delete, but
                // stamp it so the query stops returning it every day.
                prunedIds.Add(candidate.ImageId);
                continue;
            }

            if (!storage.IsPrunable(path))
            {
                logger.LogWarning(
                    "Retention: refusing to delete image {ImageId} at {Path} — outside the prunable areas",
                    candidate.ImageId, path);
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deletedFiles++;
                }
                prunedIds.Add(candidate.ImageId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or permission-denied: leave the row unstamped and try again tomorrow.
                logger.LogWarning(ex, "Retention: could not delete image file {Path}", path);
            }
        }

        await operations.MarkImageFilesPrunedAsync(prunedIds, ct);
        logger.LogInformation(
            "Retention: deleted {Files} image file(s) and marked {Rows} row(s) pruned "
            + "(generated >{GeneratedDays}d, uploads >{UploadDays}d, published >{PublishedDays}d)",
            deletedFiles, prunedIds.Count, generatedDays, uploadDays, publishedDays);
    }
}
