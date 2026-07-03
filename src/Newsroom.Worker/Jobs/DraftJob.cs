using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Infrastructure.Ai;
using Newsroom.Infrastructure.Images;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Generates article drafts for Hot topics (docs/02-functional-spec.md §4): bundle the topic's
/// sources, draft in Bulgarian per the embedded style guide, gate through
/// <see cref="DraftValidator"/>, self-check against the sources (non-fatal), attach stock-image
/// suggestions and store as PendingReview — the Telegram review surface picks drafts up in
/// Phase 4. Throttled and budgeted like every AI stage (ADR-0010); the <see cref="IDraftingAi"/>
/// is Lazy so a missing API key degrades to a skipped stage instead of failing the host.
/// </summary>
public sealed class DraftJob(
    IDraftRepository drafts,
    IAiBudget budget,
    Lazy<IDraftingAi> draftingAi,
    ImageSuggestionService imageSuggestions,
    IConfiguration configuration,
    ILogger<DraftJob> logger) : BackgroundService
{
    private const string Stage = "Draft";
    private const string SelfCheckStage = "SelfCheck";
    private const string PromptVersion = "draft-v1";

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

        var maxAttempts = configuration.GetValue("Ai:Stages:Draft:MaxAttempts", 2);
        IReadOnlyList<(long TopicId, string Label)> topics;
        try
        {
            var maxPerCycle = configuration.GetValue("Ai:Stages:Draft:MaxPerCycle", 2);
            topics = await drafts.GetHotTopicsNeedingDraftAsync(maxAttempts, maxPerCycle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not query Hot topics needing a draft");
            return; // the DB is unwell; drafting now would fail too
        }
        if (topics.Count == 0)
            return;

        var options = GeminiDraftingOptions.From(configuration);
        foreach (var (topicId, label) in topics)
        {
            ct.ThrowIfCancellationRequested();

            bool hasBudget;
            try
            {
                hasBudget = await budget.TryReserveAsync(Stage, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not check the AI request budget for stage {Stage}", Stage);
                return; // the DB is unwell; drafting now would fail too
            }
            if (!hasBudget)
            {
                logger.LogWarning("Daily AI request budget for stage {Stage} exhausted; skipping cycle", Stage);
                return;
            }

            try
            {
                await DraftTopicAsync(topicId, label, options, maxAttempts, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Draft generation failed for topic {TopicId} ({Label})", topicId, label);
                await TryRecordFailureAsync(topicId, ex.Message, maxAttempts, ct);
            }
        }
    }

    private async Task DraftTopicAsync(
        long topicId, string label, GeminiDraftingOptions options, int maxAttempts, CancellationToken ct)
    {
        var maxArticles = configuration.GetValue("Ai:Stages:Draft:MaxArticlesPerBundle", 6);
        var maxTextChars = configuration.GetValue("Ai:Stages:Draft:MaxTextCharsPerArticle", 6000);
        var bundle = await drafts.GetTopicBundleAsync(topicId, maxArticles, maxTextChars, ct);
        if (bundle is null || bundle.Articles.Count == 0)
        {
            logger.LogWarning("Topic {TopicId} ({Label}) has no source articles to draft from", topicId, label);
            await TryRecordFailureAsync(topicId, "Topic has no source articles.", maxAttempts, ct);
            return;
        }

        var generation = await draftingAi.Value.GenerateAsync(bundle, ct);
        await budget.RecordAsync(Stage, generation.Usage, ct);
        var content = generation.Content;

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
        var flaggedClaims = content.FlaggedClaims
            .Concat(unsupportedClaims)
            .Where(claim => !string.IsNullOrWhiteSpace(claim))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var images = await imageSuggestions.SuggestAsync(content.ImageSearchQueries, ct);

        // The draft row carries what the whole draft cost (generation + self-check);
        // nw_CostLedger keeps the per-stage truth.
        var usage = generation.Usage with
        {
            TokensIn = generation.Usage.TokensIn + (selfCheckUsage?.TokensIn ?? 0),
            TokensOut = generation.Usage.TokensOut + (selfCheckUsage?.TokensOut ?? 0),
            Cost = generation.Usage.Cost + (selfCheckUsage?.Cost ?? 0),
        };
        await drafts.SaveDraftAsync(bundle, content, usage, flaggedClaims, images, PromptVersion, ct);

        // Telegram pickup lands in Phase 4; the warning keeps ops visibility until then.
        logger.LogWarning("📝 Draft ready for review: {Headline} (topic {TopicId}, {Images} image suggestion(s))",
            content.Headline, topicId, images.Count);
    }

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
}
