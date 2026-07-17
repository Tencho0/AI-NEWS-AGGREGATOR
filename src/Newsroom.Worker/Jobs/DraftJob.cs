using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Core.Operations;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Ai;
using Newsroom.Infrastructure.Images;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Generates article drafts for Hot topics (docs/02-functional-spec.md §4): bundle the topic's
/// sources, draft in Bulgarian per the embedded style guide, gate through
/// <see cref="DraftValidator"/>, self-check against the sources (non-fatal), attach stock-image
/// suggestions and store as PendingReview for the Telegram review surface (TelegramJob).
/// Editor-requested regenerations (✏️ Промени) run first each cycle, and the editor's /pause
/// flag skips all generation (scraping/analysis unaffected). Throttled and budgeted like every
/// AI stage (ADR-0010); the <see cref="IDraftingAi"/> is Lazy so a missing API key degrades to
/// a skipped stage instead of failing the host.
/// </summary>
public sealed class DraftJob(
    IDraftRepository drafts,
    IReviewRepository reviews,
    IAiBudget budget,
    Lazy<IDraftingAi> draftingAi,
    ImageSuggestionService imageSuggestions,
    IJobHeartbeat heartbeat,
    IConfiguration configuration,
    ILogger<DraftJob> logger) : BackgroundService
{
    private const string Stage = "Draft";
    private const string SelfCheckStage = "SelfCheck";
    private const string PromptVersion = "draft-v2";

    private bool missingKeyWarned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(
            configuration.GetValue("Ai:Stages:Draft:CheckSeconds", 300));
        using var timer = new PeriodicTimer(checkInterval);

        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
                await heartbeat.BeatAsync(JobNames.Draft, stoppingToken);
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
        if (!GeminiChatClientFactory.HasApiKey(configuration))
        {
            if (!missingKeyWarned)
            {
                logger.LogWarning("Draft generation disabled: no API key (Ai:Gemini:ApiKey / GOOGLE_API_KEY)");
                missingKeyWarned = true;
            }
            return;
        }

        try
        {
            if (await reviews.GetRuntimeFlagAsync(TelegramJob.DraftPausedKey, false, ct))
            {
                logger.LogDebug("Draft generation paused by the editor (/pause); skipping cycle");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not read the {Flag} runtime flag", TelegramJob.DraftPausedKey);
            return; // the DB is unwell; drafting now would fail too
        }

        var options = GeminiDraftingOptions.From(configuration);
        var maxPerCycle = configuration.GetValue("Ai:Stages:Draft:MaxPerCycle", 2);

        // Regenerations first: an editor is actively waiting on ✏️ Промени.
        await RegenerateRequestedAsync(options, maxPerCycle, ct);
        await DraftHotTopicsAsync(options, maxPerCycle, ct);
    }

    private async Task DraftHotTopicsAsync(
        GeminiDraftingOptions options, int maxPerCycle, CancellationToken ct)
    {
        var maxAttempts = configuration.GetValue("Ai:Stages:Draft:MaxAttempts", 2);
        IReadOnlyList<(long TopicId, string Label)> topics;
        try
        {
            topics = await drafts.GetTopicsNeedingDraftAsync(maxAttempts, maxPerCycle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not query topics needing a draft");
            return; // the DB is unwell; drafting now would fail too
        }
        if (topics.Count == 0)
            return;

        foreach (var (topicId, label) in topics)
        {
            ct.ThrowIfCancellationRequested();

            if (!await TryReserveDraftBudgetAsync(ct))
                return;

            try
            {
                await DraftTopicAsync(topicId, label, options, maxAttempts, ct);
            }
            catch (Exception ex) when (AiTransientErrors.IsTransient(ex))
            {
                // Provider quota, overload or an empty completion, not this draft's fault: no
                // attempt burned, retry next cycle once capacity frees up (risk R-11).
                logger.LogWarning("AI temporarily unavailable while drafting topic {TopicId}; will retry later: {Reason}",
                    topicId, ex.Message);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Draft generation failed for topic {TopicId} ({Label})", topicId, label);
                await TryRecordFailureAsync(topicId, ex.Message, maxAttempts, ct);
            }
        }
    }

    private async Task RegenerateRequestedAsync(
        GeminiDraftingOptions options, int maxPerCycle, CancellationToken ct)
    {
        IReadOnlyList<PendingRegeneration> regenerations;
        try
        {
            regenerations = await drafts.GetPendingRegenerationsAsync(maxPerCycle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not query pending draft regenerations");
            return; // the DB is unwell; drafting now would fail too
        }

        foreach (var regeneration in regenerations)
        {
            ct.ThrowIfCancellationRequested();

            if (!await TryReserveDraftBudgetAsync(ct))
                return;

            try
            {
                await RegenerateDraftAsync(regeneration, options, ct);
            }
            catch (Exception ex) when (AiTransientErrors.IsTransient(ex))
            {
                // Row stays Generating with its instructions; retried automatically next cycle
                // once the quota window resets / capacity frees up.
                logger.LogWarning("AI temporarily unavailable while regenerating draft {DraftId}; will retry later: {Reason}",
                    regeneration.DraftId, ex.Message);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Regeneration failed for draft {DraftId} ({Label})",
                    regeneration.DraftId, regeneration.TopicLabel);
                await TryFailRegenerationAsync(regeneration.DraftId, ex.Message, ct);
            }
        }
    }

    private async Task DraftTopicAsync(
        long topicId, string label, GeminiDraftingOptions options, int maxAttempts, CancellationToken ct)
    {
        var bundle = await GetBundleAsync(topicId, ct);
        if (bundle is null || bundle.Articles.Count == 0)
        {
            logger.LogWarning("Topic {TopicId} ({Label}) has no source articles to draft from", topicId, label);
            await TryRecordFailureAsync(topicId, "Topic has no source articles.", maxAttempts, ct);
            return;
        }

        var generation = await draftingAi.Value.GenerateAsync(bundle, regenContext: null, ct);
        await budget.RecordAsync(Stage, generation.Usage, ct);
        var content = DraftValidator.Normalize(generation.Content);

        var violations = DraftValidator.Validate(
            content, options.Categories, options.Regions, new DraftValidationOptions());
        if (violations.Count > 0)
        {
            var joined = string.Join(" ", violations);
            logger.LogWarning("Draft for topic {TopicId} ({Label}) failed validation: {Violations}",
                topicId, label, joined);
            await TryRecordFailureAsync(topicId, joined, maxAttempts, ct);
            return;
        }

        var (unsupportedClaims, selfCheckUsage) = await SelfCheckAsync(content, bundle, ct);
        var flaggedClaims = MergeFlaggedClaims(content, unsupportedClaims);
        var images = await imageSuggestions.SuggestAsync(content.ImageSearchQueries, ct);
        var usage = CombineUsage(generation.Usage, selfCheckUsage);

        await drafts.SaveDraftAsync(bundle, content, usage, flaggedClaims, images, PromptVersion, ct);

        logger.LogInformation(
            "📝 Draft ready for review: {Headline} (topic {TopicId}, {Images} image suggestion(s))",
            content.Headline, topicId, images.Count);
    }

    private async Task RegenerateDraftAsync(
        PendingRegeneration regeneration, GeminiDraftingOptions options, CancellationToken ct)
    {
        var bundle = await GetBundleAsync(regeneration.TopicId, ct);
        if (bundle is null || bundle.Articles.Count == 0)
        {
            logger.LogWarning("Draft {DraftId} ({Label}) has no source articles to regenerate from",
                regeneration.DraftId, regeneration.TopicLabel);
            await TryFailRegenerationAsync(regeneration.DraftId, "Topic has no source articles.", ct);
            return;
        }

        var regenContext = new RegenerationContext(regeneration.Instructions, regeneration.PreviousBody);
        var generation = await draftingAi.Value.GenerateAsync(bundle, regenContext, ct);
        await budget.RecordAsync(Stage, generation.Usage, ct);
        var content = DraftValidator.Normalize(generation.Content);

        var violations = DraftValidator.Validate(
            content, options.Categories, options.Regions, new DraftValidationOptions());
        if (violations.Count > 0)
        {
            var joined = string.Join(" ", violations);
            logger.LogWarning("Regenerated draft {DraftId} ({Label}) failed validation: {Violations}",
                regeneration.DraftId, regeneration.TopicLabel, joined);
            await TryFailRegenerationAsync(regeneration.DraftId, joined, ct);
            return;
        }

        var (unsupportedClaims, selfCheckUsage) = await SelfCheckAsync(content, bundle, ct);
        var flaggedClaims = MergeFlaggedClaims(content, unsupportedClaims);
        var images = await imageSuggestions.SuggestAsync(content.ImageSearchQueries, ct);
        var usage = CombineUsage(generation.Usage, selfCheckUsage);

        // The same row flips to PendingReview with TelegramMessageId still null, so the
        // review surface posts the new version as a fresh message.
        await drafts.CompleteRegenerationAsync(
            regeneration.DraftId, bundle, content, usage, flaggedClaims, images, PromptVersion, ct);

        logger.LogInformation(
            "📝 Regenerated draft {DraftId} ready for review: {Headline} ({Images} image suggestion(s))",
            regeneration.DraftId, content.Headline, images.Count);
    }

    private Task<TopicBundle?> GetBundleAsync(long topicId, CancellationToken ct)
    {
        var maxArticles = configuration.GetValue("Ai:Stages:Draft:MaxArticlesPerBundle", 6);
        var maxTextChars = configuration.GetValue("Ai:Stages:Draft:MaxTextCharsPerArticle", 6000);
        return drafts.GetTopicBundleAsync(topicId, maxArticles, maxTextChars, ct);
    }

    private async Task<bool> TryReserveDraftBudgetAsync(CancellationToken ct)
    {
        bool hasBudget;
        try
        {
            hasBudget = await budget.TryReserveAsync(Stage, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not check the AI request budget for stage {Stage}", Stage);
            return false; // the DB is unwell; drafting now would fail too
        }
        if (!hasBudget)
            logger.LogWarning("Daily AI request budget for stage {Stage} exhausted; skipping cycle", Stage);
        return hasBudget;
    }

    /// <summary>Generation flags + self-check flags, both shown to the editor (⚠️ За проверка).</summary>
    private static IReadOnlyList<string> MergeFlaggedClaims(
        DraftContent content, IReadOnlyList<string> unsupportedClaims) =>
        content.FlaggedClaims
            .Concat(unsupportedClaims)
            .Where(claim => !string.IsNullOrWhiteSpace(claim))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The draft row carries what the whole draft cost (generation + self-check);
    /// nw_CostLedger keeps the per-stage truth.</summary>
    private static AiUsage CombineUsage(AiUsage generation, AiUsage? selfCheck) => generation with
    {
        TokensIn = generation.TokensIn + (selfCheck?.TokensIn ?? 0),
        TokensOut = generation.TokensOut + (selfCheck?.TokensOut ?? 0),
        Cost = generation.Cost + (selfCheck?.Cost ?? 0),
    };

    /// <summary>Self-check is a best-effort hallucination gate: any failure (or an exhausted
    /// SelfCheck budget) is non-fatal — the draft proceeds with only its generation flags.</summary>
    private async Task<(IReadOnlyList<string> Claims, AiUsage? Usage)> SelfCheckAsync(
        DraftContent content, TopicBundle bundle, CancellationToken ct)
    {
        try
        {
            if (!await budget.TryReserveAsync(SelfCheckStage, ct))
            {
                logger.LogWarning(
                    "Daily AI request budget for stage {Stage} exhausted; draft proceeds unchecked",
                    SelfCheckStage);
                return ([], null);
            }

            var result = await draftingAi.Value.SelfCheckAsync(content, bundle, ct);
            await budget.RecordAsync(SelfCheckStage, result.Usage, ct);
            return (result.UnsupportedClaims, result.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Draft self-check failed for topic {TopicId}; draft proceeds unchecked",
                bundle.TopicId);
            return ([], null);
        }
    }

    private async Task TryRecordFailureAsync(long topicId, string error, int maxAttempts, CancellationToken ct)
    {
        try
        {
            var exhausted = await drafts.RecordGenerationFailureAsync(topicId, error, maxAttempts, ct);
            if (exhausted)
                logger.LogWarning("Topic {TopicId} reached {MaxAttempts} failed draft attempt(s); giving up",
                    topicId, maxAttempts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record failed draft attempt for topic {TopicId}", topicId);
        }
    }

    private async Task TryFailRegenerationAsync(long draftId, string error, CancellationToken ct)
    {
        try
        {
            await drafts.FailRegenerationAsync(draftId, error, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record failed regeneration for draft {DraftId}", draftId);
        }
    }
}
