using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class AiTransientErrorsTests
{
    [Fact]
    public void Empty_completion_is_transient()
    {
        // The 200-shaped sibling of the 503 "high demand" error (observed under provider load):
        // retried on a later cycle without burning the item's attempt budget.
        Assert.True(AiTransientErrors.IsTransient(new AiEmptyResponseException("analysis batch", "stop")));
    }

    [Fact]
    public void Prompt_blocked_empty_completion_is_not_transient()
    {
        // promptFeedback.blockReason (e.g. PROHIBITED_CONTENT) is deterministic for the same
        // batch: treating it as transient re-fetched and re-blocked the identical oldest-first
        // batch forever and froze the Analyse queue (2026-07-16). Burning the attempt lets
        // poison protection mark the articles Ignored and the queue drain.
        Assert.False(AiTransientErrors.IsTransient(
            new AiEmptyResponseException("analysis batch", null, "PROHIBITED_CONTENT")));
    }

    [Fact]
    public void Content_filtered_empty_completion_is_not_transient()
    {
        // A candidate-level safety block (finishReason=SAFETY → content_filter) is the same
        // permanent, content-determined refusal as a prompt block — only reported one level lower.
        Assert.False(AiTransientErrors.IsTransient(
            new AiEmptyResponseException("analysis batch", "content_filter")));
    }

    [Theory]
    [InlineData("You exceeded your current quota, please check your plan and billing details.")]
    [InlineData("Error 429: RESOURCE_EXHAUSTED")]
    [InlineData("This model is currently experiencing high demand. Spikes in demand are usually temporary.")]
    [InlineData("The model is overloaded. Please try again later.")]
    [InlineData("HTTP 503 Service Unavailable")]
    [InlineData("Status(StatusCode=\"Unavailable\")")]
    public void Provider_fault_wordings_are_transient(string message) =>
        Assert.True(AiTransientErrors.IsTransient(new InvalidOperationException(message)));

    [Fact]
    public void Ordinary_failures_are_not_transient() =>
        Assert.False(AiTransientErrors.IsTransient(
            new InvalidOperationException("AI returned malformed JSON for the analysis batch: oops")));

    [Theory]
    [InlineData("Error 429: RESOURCE_EXHAUSTED. Quota exceeded for quota id: GenerateRequestsPerDayPerProjectPerModel-FreeTier.")]
    [InlineData("You exceeded your current quota: 20 requests per day for this model.")]
    public void Daily_quota_wordings_are_daily_exhaustion(string message)
    {
        var ex = new InvalidOperationException(message);
        Assert.True(AiTransientErrors.IsDailyQuotaExhausted(ex));
        Assert.True(AiTransientErrors.IsTransient(ex)); // still transient for job-level catches
    }

    [Theory]
    [InlineData("Error 429: RESOURCE_EXHAUSTED. Quota exceeded for quota id: GenerateRequestsPerMinutePerProjectPerModel-FreeTier.")]
    [InlineData("Error 429: RESOURCE_EXHAUSTED")]
    [InlineData("The model is overloaded. Please try again later.")]
    [InlineData("HTTP 503 Service Unavailable")]
    [InlineData("AI returned malformed JSON for the analysis batch: oops")]
    public void Non_daily_failures_are_not_daily_exhaustion(string message) =>
        Assert.False(AiTransientErrors.IsDailyQuotaExhausted(new InvalidOperationException(message)));
}
