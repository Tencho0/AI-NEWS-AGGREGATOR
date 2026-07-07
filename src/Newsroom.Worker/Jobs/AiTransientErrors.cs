namespace Newsroom.Worker.Jobs;

/// <summary>
/// Classifies Google GenAI failures that are the provider's fault, not the item's, so a job can
/// retry the item on a later cycle without burning its attempt budget. String-matched because the
/// Google SDK surfaces these as generic exceptions (see docs/07-operations.md retry taxonomy;
/// risk R-11). Used by <see cref="AnalyseJob"/> and <see cref="DraftJob"/>; clustering (TrendJob)
/// has no per-item attempts, so a transient failure there is already harmless.
/// </summary>
internal static class AiTransientErrors
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

    /// <summary>Either kind: retry on a later cycle, do not burn the item's attempt.</summary>
    public static bool IsTransient(Exception ex) => IsQuotaExhausted(ex) || IsProviderOverloaded(ex);
}
