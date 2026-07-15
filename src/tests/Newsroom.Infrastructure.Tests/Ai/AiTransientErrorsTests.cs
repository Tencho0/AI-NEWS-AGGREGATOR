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
}
