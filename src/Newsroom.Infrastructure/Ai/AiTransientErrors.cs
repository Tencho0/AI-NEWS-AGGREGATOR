namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Classifies Google GenAI failures that are the provider's fault, not the item's, so a job can
/// retry the item on a later cycle without burning its attempt budget. String-matched because the
/// Google SDK surfaces these as generic exceptions (see docs/07-operations.md retry taxonomy;
/// risk R-11). Used by the Worker's AnalyseJob and DraftJob; clustering (TrendJob) has no
/// per-item attempts, so a transient failure there is already harmless.
/// </summary>
public static class AiTransientErrors
{
    /// <summary>Quota / rate exhaustion (Gemini free tier resets daily): "quota", HTTP 429,
    /// RESOURCE_EXHAUSTED wordings.</summary>
    public static bool IsQuotaExhausted(Exception ex) =>
        ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal)
        || ex.Message.Contains("429", StringComparison.Ordinal);

    /// <summary>Daily-quota exhaustion specifically (Gemini free tier, resets at midnight
    /// US-Pacific): a quota 429 whose message names a per-day quota — Google's payload carries
    /// quota ids like "GenerateRequestsPerDayPerProjectPerModel-FreeTier". Per-minute/per-token
    /// 429s deliberately do NOT match (falling back for a whole day over an RPM blip would be
    /// wrong); when the wording is ambiguous this returns false and behaviour stays as today.
    /// Used by GeminiQuotaFallbackChatClient to switch a stage to the Analyse model.</summary>
    public static bool IsDailyQuotaExhausted(Exception ex) =>
        IsQuotaExhausted(ex)
        && (ex.Message.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("per day", StringComparison.OrdinalIgnoreCase));

    /// <summary>Transient provider-side unavailability (HTTP 503 "high demand" / overloaded /
    /// UNAVAILABLE): Google's capacity, not the item's fault.</summary>
    public static bool IsProviderOverloaded(Exception ex) =>
        ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("503", StringComparison.Ordinal);

    /// <summary>Provider fault of any kind — quota, overload, or a load-shaped empty completion
    /// (the 200-shaped sibling of the 503, see <see cref="AiEmptyResponseException"/>): retry on
    /// a later cycle, do not burn the item's attempt. A content-blocked empty is NOT transient —
    /// it is deterministic for the same input, and retrying it without burning attempts froze the
    /// Analyse queue on 2026-07-16 (same oldest-first batch re-fetched and re-blocked forever).</summary>
    public static bool IsTransient(Exception ex) =>
        IsQuotaExhausted(ex) || IsProviderOverloaded(ex)
        || (ex is AiEmptyResponseException empty && !IsContentBlocked(empty));

    /// <summary>Content-determined refusal: a prompt-level block (promptFeedback.blockReason) or
    /// a candidate-level safety block (finishReason SAFETY → "content_filter"). Permanent for the
    /// batch, so the attempt must burn and poison protection can eventually ignore the items.</summary>
    private static bool IsContentBlocked(AiEmptyResponseException ex) =>
        ex.BlockReason is not null
        || string.Equals(ex.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase);
}
