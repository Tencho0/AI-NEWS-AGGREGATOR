using Microsoft.Extensions.AI;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Delegating <see cref="IChatClient"/> that keeps a stage alive when its Gemini model's
/// free-tier daily quota runs out: on a daily-quota 429 (never a per-minute one — see
/// <see cref="AiTransientErrors.IsDailyQuotaExhausted"/>) it activates the shared per-model
/// <see cref="GeminiModelFallback"/> and retries the same request once on the Analyse stage's
/// model, so the triggering cycle succeeds instead of being wasted. While the window is active
/// every request routes to the fallback; after the Pacific-midnight reset the primary is
/// probed again. Gemini-only by construction — non-Gemini stages are never wrapped
/// (<see cref="GeminiChatClientFactory"/>). The response's ModelId is stamped with the model
/// actually used so nw_CostLedger records fallback usage truthfully.
/// </summary>
public sealed class GeminiQuotaFallbackChatClient(
    IChatClient primary,
    string primaryModel,
    IChatClient fallback,
    string fallbackModel,
    GeminiModelFallback state) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialise once: on the activate-and-retry path the sequence is enumerated twice.
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        if (state.IsActive(primaryModel))
            return WithModelId(
                await fallback.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false),
                fallbackModel);

        try
        {
            return WithModelId(
                await primary.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false),
                primaryModel);
        }
        catch (Exception ex) when (AiTransientErrors.IsDailyQuotaExhausted(ex))
        {
            state.Activate(primaryModel);
            return WithModelId(
                await fallback.GetResponseAsync(messageList, options, cancellationToken).ConfigureAwait(false),
                fallbackModel);
        }
    }

    /// <summary>Routing only, no catch-and-retry: nothing in this codebase streams today.</summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        (state.IsActive(primaryModel) ? fallback : primary)
            .GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        primary.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        primary.Dispose();
        fallback.Dispose();
    }

    private static ChatResponse WithModelId(ChatResponse response, string model)
    {
        response.ModelId ??= model;
        return response;
    }
}
