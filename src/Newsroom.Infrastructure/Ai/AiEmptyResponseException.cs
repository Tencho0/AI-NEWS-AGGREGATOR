namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// The model returned an HTTP 200 with an empty completion — observed under provider load as the
/// 200-shaped sibling of the 503 "high demand" error, so it is classified as transient
/// (<see cref="AiTransientErrors"/>) and must not burn the item's attempt budget. The finish
/// reason is carried so a genuinely permanent empty (e.g. a SAFETY block) is diagnosable from
/// the log instead of invisible.
/// </summary>
public sealed class AiEmptyResponseException(string what, string? finishReason) : Exception(
    $"AI returned an empty completion for the {what} (finishReason: {finishReason ?? "unknown"})")
{
    public string? FinishReason { get; } = finishReason;
}
