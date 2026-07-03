using Newsroom.Core.Ai;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Ai;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Detects trends (docs/02-functional-spec.md §3) over a sliding window: a free deterministic
/// wire-copy pre-pass, then AI-assisted clustering of analysed articles into topics (ADR-0010:
/// one request per batch, throttled and budgeted), then trend scoring. Scoring runs even
/// without an API key, so already-clustered topics keep decaying; the <see cref="IClusteringAi"/>
/// is Lazy for the same reason as the analysis client — only materialised once a key exists.
/// </summary>
public sealed class TrendJob(
    ITopicRepository topics,
    IAiBudget budget,
    Lazy<IClusteringAi> clusteringAi,
    IConfiguration configuration,
    ILogger<TrendJob> logger) : BackgroundService
{
    private const string Stage = "Cluster";

    private bool missingKeyWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(
            configuration.GetValue("Ai:Stages:Cluster:CheckSeconds", 300));
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
        var nowUtc = DateTime.UtcNow;
        var windowStartUtc = nowUtc.AddHours(-configuration.GetValue("Trend:WindowHours", 48));

        try
        {
            var wireCopies = await topics.AssignWireCopyDuplicatesAsync(windowStartUtc, ct);
            if (wireCopies > 0)
                logger.LogInformation("Assigned {Count} wire-copy duplicate(s) to existing topics", wireCopies);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Wire-copy pre-pass failed");
            return; // the DB is unwell; clustering and scoring now would fail too
        }

        await ClusterAsync(windowStartUtc, ct);
        await ScoreAsync(nowUtc, windowStartUtc, ct);
    }

    private async Task ClusterAsync(DateTime windowStartUtc, CancellationToken ct)
    {
        if (!GeminiChatClientFactory.HasApiKey(configuration))
        {
            if (!missingKeyWarned)
            {
                logger.LogWarning("Topic clustering disabled: no API key (Ai:Gemini:ApiKey / GOOGLE_API_KEY)");
                missingKeyWarned = true;
            }
            return;
        }

        try
        {
            var batchSize = configuration.GetValue("Ai:Stages:Cluster:BatchSize", 30);
            var candidates = await topics.GetUnassignedCandidatesAsync(windowStartUtc, batchSize, ct);
            if (candidates.Count == 0)
                return;

            if (!await budget.TryReserveAsync(Stage, ct))
            {
                logger.LogWarning("Daily AI request budget for stage {Stage} exhausted; skipping cycle", Stage);
                return;
            }

            var recentTitlesPerTopic = configuration.GetValue("Ai:Stages:Cluster:RecentTitlesPerTopic", 3);
            var snapshots = await topics.GetOpenTopicSnapshotsAsync(recentTitlesPerTopic, ct);

            var result = await clusteringAi.Value.AssignAsync(snapshots, candidates, ct);
            await topics.ApplyAssignmentsAsync(result.Assignments, ct);
            await budget.RecordAsync(Stage, result.Usage, ct);

            var newTopics = result.Assignments
                .Where(a => a.NewTopicLabel is not null)
                .Select(a => a.NewTopicLabel!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            logger.LogInformation("Clustered {Count} article(s) into topics ({New} new topic(s))",
                result.Assignments.Count, newTopics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Clustering step failed"); // scoring still runs on what we have
        }
    }

    private async Task ScoreAsync(DateTime nowUtc, DateTime windowStartUtc, CancellationToken ct)
    {
        try
        {
            var options = ScorerOptionsFromConfiguration();
            var hotThreshold = configuration.GetValue("Trend:HotThreshold", 6.0);

            var openTopics = await topics.GetOpenTopicsAsync(ct);
            var factsByTopic = await topics.GetOpenTopicFactsAsync(ct);

            foreach (var topic in openTopics)
            {
                ct.ThrowIfCancellationRequested();

                var facts = factsByTopic.GetValueOrDefault(topic.TopicId, []);
                var score = TrendScorer.Compute(nowUtc, facts, options);
                var status = topic.Status;

                if (facts.Count > 0 && facts.Max(f => f.SeenAtUtc) < windowStartUtc)
                {
                    status = TopicStatus.Done; // the story left the sliding window
                }
                else if (topic.MutedUntilUtc > nowUtc)
                {
                    // muted by the editor: keep the status, but keep the score honest
                }
                else if (score >= hotThreshold && topic.Status != TopicStatus.Hot)
                {
                    status = TopicStatus.Hot;
                    // Telegram alert lands in Phase 4; the warning keeps ops visibility until then.
                    logger.LogWarning("🔥 HOT topic {Label} (score {Score:F1})", topic.Label, score);
                }
                // score < threshold on a Hot topic keeps Hot: no demotion in v1, the draft
                // lifecycle (Phase 4) owns what happens to a cooling story.

                await topics.UpdateTopicAsync(topic.TopicId, score, status, nowUtc, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Trend scoring failed");
        }
    }

    private TrendScorerOptions ScorerOptionsFromConfiguration()
    {
        var defaults = new TrendScorerOptions();
        return new TrendScorerOptions
        {
            ArticlesWeight = configuration.GetValue("Trend:ArticlesWeight", defaults.ArticlesWeight),
            SourcesWeight = configuration.GetValue("Trend:SourcesWeight", defaults.SourcesWeight),
            VelocityWeight = configuration.GetValue("Trend:VelocityWeight", defaults.VelocityWeight),
            RegionWeight = configuration.GetValue("Trend:RegionWeight", defaults.RegionWeight),
            HalfLifeHours = configuration.GetValue("Trend:HalfLifeHours", defaults.HalfLifeHours),
            VelocityWindowHours = configuration.GetValue("Trend:VelocityWindowHours", defaults.VelocityWindowHours),
        };
    }
}
