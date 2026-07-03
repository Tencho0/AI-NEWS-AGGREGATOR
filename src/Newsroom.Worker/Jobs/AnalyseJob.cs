using Newsroom.Core.Ai;
using Newsroom.Core.Operations;
using Newsroom.Infrastructure.Ai;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Summarises and classifies scraped articles in batches (ADR-0010): one AI request per batch,
/// throttled and budgeted because free-tier quotas are the binding constraint. Without an API
/// key the stage degrades to a no-op sweep instead of failing the host, which is why the
/// <see cref="IAiClient"/> is Lazy — it is only materialised once a key exists.
/// </summary>
public sealed class AnalyseJob(
    IAnalysisRepository repository,
    IAiBudget budget,
    Lazy<IAiClient> aiClient,
    IJobHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<AnalyseJob> logger) : BackgroundService
{
    private const string Stage = "Analyse";

    private bool missingKeyWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(
            configuration.GetValue("Ai:Stages:Analyse:CheckSeconds", 120));
        using var timer = new PeriodicTimer(checkInterval);

        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
                await heartbeat.BeatAsync(JobNames.Analyse, stoppingToken);
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
            var swept = await repository.MarkUnanalysableAsync(ct);
            if (swept > 0)
                logger.LogInformation("Ignored {Count} article(s) with too little text to analyse", swept);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unanalysable-article sweep failed");
            return; // the DB is unwell; analysing now would fail too
        }

        if (!GeminiChatClientFactory.HasApiKey(configuration))
        {
            if (!missingKeyWarned)
            {
                logger.LogWarning("AI analysis disabled: no API key (Ai:Gemini:ApiKey / GOOGLE_API_KEY)");
                missingKeyWarned = true;
            }
            return;
        }

        var maxAttempts = configuration.GetValue("Ai:Stages:Analyse:MaxAttempts", 3);
        List<long> batchIds = [];
        try
        {
            if (!await budget.TryReserveAsync(Stage, ct))
            {
                logger.LogWarning("Daily AI request budget for stage {Stage} exhausted; skipping cycle", Stage);
                return;
            }

            var batchSize = configuration.GetValue("Ai:Stages:Analyse:BatchSize", 8);
            var batch = await repository.GetBatchAsync(batchSize, maxAttempts, ct);
            if (batch.Count == 0)
                return;
            batchIds = [.. batch.Select(a => a.ArticleId)];

            var result = await aiClient.Value.SummariseAndClassifyAsync(batch, ct);
            await repository.SaveAsync(result.Results, result.Usage, ct);
            await budget.RecordAsync(Stage, result.Usage, ct);

            // Articles the model skipped stay unanalysed; count the attempt so a persistently
            // skipped article cannot burn budget forever.
            var analysed = result.Results.Select(r => r.ArticleId).ToHashSet();
            var missing = batchIds.Where(id => !analysed.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                logger.LogWarning("AI response missed {Count} of {Total} article(s) in the batch",
                    missing.Count, batchIds.Count);
                await repository.MarkFailedAttemptAsync(missing, maxAttempts, ct);
            }

            logger.LogInformation(
                "Analysed {Count} article(s) with {Model} ({TokensIn} in / {TokensOut} out tokens)",
                result.Results.Count, result.Usage.Model, result.Usage.TokensIn, result.Usage.TokensOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Analyse cycle failed for {Count} article(s)", batchIds.Count);
            await TryMarkFailedAttemptAsync(batchIds, maxAttempts, ct);
        }
    }

    private async Task TryMarkFailedAttemptAsync(List<long> articleIds, int maxAttempts, CancellationToken ct)
    {
        try
        {
            await repository.MarkFailedAttemptAsync(articleIds, maxAttempts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record failed analysis attempt for {Count} article(s)",
                articleIds.Count);
        }
    }
}
