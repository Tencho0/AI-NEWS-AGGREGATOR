using Microsoft.Extensions.Configuration;

using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiChatClientFactoryTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> CurrentShape() => new()
    {
        ["Ai:Stages:Analyse:Model"] = "gemini-3.1-flash-lite",
        ["Ai:Stages:Cluster:Model"] = "gemini-2.5-flash",
        ["Ai:Stages:Draft:Model"] = "gemini-3.5-flash",
        ["Ai:Stages:SelfCheck:Model"] = "gemini-3.5-flash",
    };

    [Theory]
    [InlineData("Cluster")]
    [InlineData("Draft")]
    [InlineData("SelfCheck")]
    public void Wraps_gemini_stages_with_no_provider_key(string stage) =>
        // No Ai:Stages:{stage}:Provider keys exist in today's config: absent means Gemini
        // (ADR-0010), so the current production shape must get the fallback.
        Assert.True(GeminiChatClientFactory.ShouldUseFallback(Config(CurrentShape()), stage));

    [Fact]
    public void Never_wraps_a_non_gemini_stage()
    {
        var values = CurrentShape();
        values["Ai:Stages:Draft:Provider"] = "anthropic";
        Assert.False(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Draft"));
        Assert.True(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster")); // others unaffected
    }

    [Fact]
    public void Never_wraps_when_the_analyse_fallback_target_is_not_gemini()
    {
        var values = CurrentShape();
        values["Ai:Stages:Analyse:Provider"] = "openai";
        Assert.False(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster"));
    }

    [Fact]
    public void Provider_match_is_case_insensitive()
    {
        var values = CurrentShape();
        values["Ai:Stages:Cluster:Provider"] = "Gemini";
        Assert.True(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster"));
    }

    [Fact]
    public void Never_wraps_a_stage_already_on_the_analyse_model()
    {
        var values = CurrentShape();
        values["Ai:Stages:Cluster:Model"] = "gemini-3.1-flash-lite";
        Assert.False(GeminiChatClientFactory.ShouldUseFallback(Config(values), "Cluster"));
    }

    // The Gemini clients are built by the Google SDK rather than AddHttpClient, so their timeout
    // is the one the worker states here — it is not covered by any Polly handler.

    [Fact]
    public void Request_timeout_defaults_to_the_inherited_hundred_seconds_in_milliseconds()
    {
        var options = GeminiChatClientFactory.HttpOptionsFor(Config(CurrentShape()));
        Assert.Equal(GeminiChatClientFactory.DefaultRequestTimeoutSeconds * 1000, options?.Timeout);
    }

    [Fact]
    public void Request_timeout_is_configurable_in_seconds()
    {
        var values = CurrentShape();
        values["Ai:RequestTimeoutSeconds"] = "45";
        Assert.Equal(45_000, GeminiChatClientFactory.HttpOptionsFor(Config(values))?.Timeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Non_positive_timeout_leaves_the_sdk_default_alone(string configured)
    {
        // Handing the SDK Timeout = 0 would cancel every request instantly; opting out has to
        // mean "don't set one".
        var values = CurrentShape();
        values["Ai:RequestTimeoutSeconds"] = configured;
        Assert.Null(GeminiChatClientFactory.HttpOptionsFor(Config(values)));
    }
}
