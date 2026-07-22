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
}
