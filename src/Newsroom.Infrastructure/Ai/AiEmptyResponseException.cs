namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// The model returned an HTTP 200 with an empty completion. Two very different causes share this
/// shape: provider load (the 200-shaped sibling of the 503 "high demand" error — transient, retry
/// without burning the item's attempt budget) and a content block (<paramref name="blockReason"/>
/// from promptFeedback, or a candidate-level SAFETY finish — deterministic for the same input, so
/// retrying forever would freeze the queue, as it did on 2026-07-16). <see cref="AiTransientErrors"/>
/// uses <see cref="BlockReason"/> and <see cref="FinishReason"/> to tell them apart.
/// </summary>
public sealed class AiEmptyResponseException(string what, string? finishReason, string? blockReason = null)
    : Exception(BuildMessage(what, finishReason, blockReason))
{
    public string? FinishReason { get; } = finishReason;

    /// <summary>Prompt-level block reason (e.g. PROHIBITED_CONTENT), null when not blocked.</summary>
    public string? BlockReason { get; } = blockReason;

    private static string BuildMessage(string what, string? finishReason, string? blockReason) =>
        blockReason is null
            ? $"AI returned an empty completion for the {what} (finishReason: {finishReason ?? "unknown"})"
            : $"AI blocked the {what} prompt (blockReason: {blockReason})";
}
