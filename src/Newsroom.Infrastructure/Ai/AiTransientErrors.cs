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

    /// <summary>Transient provider-side unavailability (HTTP 503 "high demand" / overloaded /
    /// UNAVAILABLE): Google's capacity, not the item's fault.</summary>
    public static bool IsProviderOverloaded(Exception ex) =>
        ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("503", StringComparison.Ordinal);

    /// <summary>Provider fault of any kind — quota, overload, or an empty completion (the
    /// 200-shaped sibling of the 503, see <see cref="AiEmptyResponseException"/>): retry on a
    /// later cycle, do not burn the item's attempt.</summary>
    public static bool IsTransient(Exception ex) =>
        IsQuotaExhausted(ex) || IsProviderOverloaded(ex) || ex is AiEmptyResponseException;
}
