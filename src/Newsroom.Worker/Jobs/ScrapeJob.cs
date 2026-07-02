using Newsroom.Core.Scraping;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Polls due sources, parses their feeds, dedups by canonical URL and stores new articles.
/// One bad source or item never stops the run (docs/07-operations.md, item-level isolation).
/// </summary>
public sealed class ScrapeJob(
    ISourceRepository sources,
    ISourceArticleRepository articles,
    IFeedReader feedReader,
    IArticleTextExtractor textExtractor,
    IRobotsPolicy robots,
    IConfiguration configuration,
    ILogger<ScrapeJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(configuration.GetValue("Scrape:CheckSeconds", 60));
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
        List<Source> due;
        try
        {
            due = [.. await sources.GetDueAsync(DateTime.UtcNow, ct)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Scrape cycle could not load due sources");
            return;
        }

        if (due.Count == 0)
        {
            logger.LogDebug("No sources due");
            return;
        }

        logger.LogInformation("Scrape cycle: {Count} source(s) due", due.Count);

        // Sequential on purpose: politeness beats throughput at our volume (docs/05/scraping.md).
        foreach (var source in due)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessSourceAsync(source, ct);
        }

        await DisableDeadSourcesAsync(ct);
    }

    private async Task ProcessSourceAsync(Source source, CancellationToken ct)
    {
        try
        {
            if (source.Kind != SourceKind.Rss)
            {
                // Sitemap/HTML listing adapters are future work; sources are data, so a wrongly
                // configured kind must not crash the run.
                await sources.RecordFailureAsync(source.Id, DateTime.UtcNow,
                    $"Source kind '{source.Kind}' is not supported yet", ct);
                return;
            }

            if (!await robots.IsAllowedAsync(new Uri(source.Url), ct))
            {
                await sources.RecordFailureAsync(source.Id, DateTime.UtcNow,
                    "Blocked by robots.txt", ct);
                logger.LogWarning("Source {Source} feed URL is disallowed by robots.txt", source.Name);
                return;
            }

            var result = await feedReader.FetchAsync(source, ct);
            if (result.NotModified)
            {
                await sources.RecordSuccessAsync(source.Id, DateTime.UtcNow, result.Etag,
                    result.LastModifiedHeader, ct);
                logger.LogDebug("Source {Source}: not modified", source.Name);
                return;
            }

            // Canonicalise up front and skip already-stored articles entirely: no page re-fetch
            // on every cycle (politeness), no churn. Consequence (documented in scraping.md):
            // post-publication edits are only picked up for feeds that carry their full text.
            var candidates = result.Items
                .Select(item => UrlCanonicalizer.TryCanonicalize(item.Link, out var canonical)
                    ? (Item: item, Url: canonical, UrlHash: HashUtil.Sha256Hex(canonical))
                    : default)
                .Where(c => c.Item is not null)
                .ToList();

            var existing = await articles.GetExistingUrlHashesAsync(
                candidates.Select(c => c.UrlHash).ToList(), ct);

            var newCount = 0;
            var fetchState = new FullFetchState();
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var feedTextIsComplete = candidate.Item.Text?.Length >=
                                         configuration.GetValue("Scrape:MinTextLength", 400);
                if (existing.Contains(candidate.UrlHash) && !feedTextIsComplete)
                    continue; // seen before, and refreshing would cost a page fetch — skip

                if (await StoreItemAsync(source, candidate.Item, candidate.Url, candidate.UrlHash,
                        fetchState, ct))
                {
                    newCount++;
                }
            }

            await sources.RecordSuccessAsync(source.Id, DateTime.UtcNow, result.Etag,
                result.LastModifiedHeader, ct);
            logger.LogInformation("Source {Source}: {Total} item(s), {New} new",
                source.Name, result.Items.Count, newCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Source {Source} failed", source.Name);
            await TryRecordFailureAsync(source, ex, ct);
        }
    }

    private async Task<bool> StoreItemAsync(
        Source source, FeedItem item, string canonicalUrl, string urlHash,
        FullFetchState fetchState, CancellationToken ct)
    {
        var text = item.Text;
        var minLength = configuration.GetValue("Scrape:MinTextLength", 400);
        if ((text is null || text.Length < minLength) && configuration.GetValue("Scrape:FetchFullText", true))
            text = await FetchFullTextAsync(source, canonicalUrl, fetchState, ct) ?? text;

        var article = new SourceArticle
        {
            SourceId = source.Id,
            Url = canonicalUrl,
            UrlHash = urlHash,
            Title = item.Title,
            Author = item.Author,
            PublishedAtUtc = item.PublishedAtUtc,
            ExtractedText = text,
            ContentHash = HashUtil.ContentHash(item.Title, text),
        };

        return await articles.UpsertAsync(article, ct);
    }

    private async Task<string?> FetchFullTextAsync(
        Source source, string url, FullFetchState fetchState, CancellationToken ct)
    {
        try
        {
            var target = new Uri(url);
            if (!await robots.IsAllowedAsync(target, ct))
            {
                logger.LogDebug("Full-text fetch disallowed by robots.txt: {Url}", url);
                return null;
            }

            // Politeness delay between successive page fetches on the same host.
            if (fetchState.AnyFetchDone)
                await Task.Delay(TimeSpan.FromSeconds(source.PolitenessDelaySeconds), ct);
            fetchState.AnyFetchDone = true;

            return await textExtractor.ExtractAsync(url, source.ParserHint, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Full-text fetch failed for {Url}; keeping feed text", url);
            return null;
        }
    }

    /// <summary>Tracks whether a full-page fetch already happened for the current source pass.</summary>
    private sealed class FullFetchState
    {
        public bool AnyFetchDone;
    }

    private async Task DisableDeadSourcesAsync(CancellationToken ct)
    {
        try
        {
            var deadCutoffHours = configuration.GetValue("Scrape:DisableAfterFailingHours", 24);
            var disabled = await sources.DisableDeadSourcesAsync(
                DateTime.UtcNow.AddHours(-deadCutoffHours), ct);
            foreach (var source in disabled)
            {
                // Surfaced to the Telegram admin thread once Phase 4 lands (docs/07-operations.md).
                logger.LogWarning("Source {Source} auto-disabled after {Hours}h of failures (last error: {Error})",
                    source.Name, deadCutoffHours, source.LastError);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dead-source sweep failed");
        }
    }

    private async Task TryRecordFailureAsync(Source source, Exception ex, CancellationToken ct)
    {
        try
        {
            await sources.RecordFailureAsync(source.Id, DateTime.UtcNow, ex.Message, ct);
        }
        catch (Exception recordEx) when (recordEx is not OperationCanceledException)
        {
            logger.LogError(recordEx, "Could not record failure for source {Source}", source.Name);
        }
    }
}
