using Microsoft.Extensions.AI;
using Newsroom.Core.Drafting;
using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiDraftingAiTests
{
    private const string ValidDraftJson =
        """
        {
          "headline": "МОЩЕН ТРУС РАЗТЪРСИ ЮГОЗАПАДА",
          "subtitle": "Земетресението бе усетено и в Благоевград",
          "bodyMarkdown": "Земетресение разтърси региона, съобщи БТА.",
          "category": "Общество",
          "region": "Благоевград",
          "tags": ["земетресение", "Благоевград"],
          "seoTitle": "Трус в Югозапада",
          "seoDescription": "Земетресение бе усетено в Благоевград и региона.",
          "imageSearchQueries": ["earthquake damage", "seismograph"],
          "imageAltTextBg": "Сеизмограф записва трус",
          "flaggedClaims": ["Магнитудът от 4.5 идва само от един източник."],
          "confidence": 0.85,
          "facebookCaption": "Земетресение разлюля Югозапада тази сутрин.\n\nТрусът е усетен в Благоевград и района, съобщи БТА. Няма данни за пострадали и щети по сградите.\n\nВие усетихте ли труса? Разкажете ни в коментарите.",
          "facebookHashtags": ["#Благоевград", "#Земетресение"]
        }
        """;

    private static TopicBundle Bundle() => new(
        42,
        "Земетресение в Югозапада",
        [
            new TopicSourceArticle(1, "Трус край Симитли", "БТА", "https://example.com/1",
                new DateTime(2026, 7, 1), "Кратко резюме на новината.", "Пълен текст на статията."),
            new TopicSourceArticle(2, "Земетресение усетено в Благоевград", "Дневник",
                "https://example.com/2", null, "Второ резюме.", null),
        ]);

    private static DraftContent Draft() => new(
        "МОЩЕН ТРУС РАЗТЪРСИ ЮГОЗАПАДА", null, "Земетресение разтърси региона.", "Общество",
        "Благоевград", ["земетресение"], "Трус в Югозапада", "Земетресение в региона.",
        ["earthquake"], null, [], 0.85, "", []);

    private static (GeminiDraftingAi Client, FakeChatClient DraftFake, FakeChatClient SelfCheckFake) CreateClient(
        string draftResponseText, string selfCheckResponseText = """{"unsupportedClaims": []}""",
        UsageDetails? usage = null, GeminiDraftingOptions? options = null)
    {
        var draftFake = new FakeChatClient(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, draftResponseText))
        {
            Usage = usage,
            ModelId = "gemini-2.5-flash",
        });
        var selfCheckFake = new FakeChatClient(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, selfCheckResponseText))
        {
            Usage = usage,
            ModelId = "gemini-2.5-flash",
        });
        var client = new GeminiDraftingAi(draftFake, selfCheckFake, options ?? new GeminiDraftingOptions(),
            new AiRateLimiter(requestsPerMinute: 1000)); // permissive: throttling is not under test
        return (client, draftFake, selfCheckFake);
    }

    [Fact]
    public async Task Generate_parses_draft_and_usage_from_clean_json()
    {
        var (client, fake, _) = CreateClient(ValidDraftJson,
            usage: new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            options: new GeminiDraftingOptions { InputPricePerMTok = 1m, OutputPricePerMTok = 2m });

        var result = await client.GenerateAsync(Bundle(), null, CancellationToken.None);

        var content = result.Content;
        Assert.Equal("МОЩЕН ТРУС РАЗТЪРСИ ЮГОЗАПАДА", content.Headline);
        Assert.Equal("Земетресението бе усетено и в Благоевград", content.Subtitle);
        Assert.Equal("Земетресение разтърси региона, съобщи БТА.", content.BodyMarkdown);
        Assert.Equal("Общество", content.Category);
        Assert.Equal("Благоевград", content.Region);
        Assert.Equal(["земетресение", "Благоевград"], content.Tags);
        Assert.Equal("Трус в Югозапада", content.SeoTitle);
        Assert.Equal(["earthquake damage", "seismograph"], content.ImageSearchQueries);
        Assert.Equal("Сеизмограф записва трус", content.ImageAltTextBg);
        Assert.Single(content.FlaggedClaims);
        Assert.Equal(0.85, content.Confidence);
        Assert.StartsWith("Земетресение разлюля Югозапада", content.FacebookCaption);
        Assert.Equal(["#Благоевград", "#Земетресение"], content.FacebookHashtags);

        Assert.Equal("gemini", result.Usage.Provider);
        Assert.Equal("gemini-2.5-flash", result.Usage.Model);
        Assert.Equal(100, result.Usage.TokensIn);
        Assert.Equal(50, result.Usage.TokensOut);
        Assert.Equal(0.0002m, result.Usage.Cost);     // (100*1 + 50*2) / 1M

        // The provider seam is asked for JSON explicitly.
        Assert.Same(ChatResponseFormat.Json, fake.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task Generate_prompt_carries_style_guide_taxonomy_and_sources()
    {
        var (client, fake, _) = CreateClient(ValidDraftJson);

        await client.GenerateAsync(Bundle(), null, CancellationToken.None);

        var systemPrompt = fake.LastMessages!.Single(m => m.Role == ChatRole.System).Text;
        Assert.Contains("Predel News", systemPrompt);
        Assert.Contains("Стилови правила", systemPrompt);   // embedded editorial style guide
        Assert.Contains("Общество", systemPrompt);          // category taxonomy
        Assert.Contains("Благоевград", systemPrompt);       // region taxonomy
        Assert.Contains("измислени факти", systemPrompt);   // hard rules

        var userPrompt = fake.LastMessages!.Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("Земетресение в Югозапада", userPrompt);  // topic label
        Assert.Contains("Трус край Симитли", userPrompt);
        Assert.Contains("БТА", userPrompt);
        Assert.Contains("Пълен текст на статията.", userPrompt);
        Assert.Contains("250-450", userPrompt);                   // target length
    }

    [Fact]
    public async Task Generate_with_regen_context_adds_instructions_and_previous_body_block()
    {
        var (client, fake, _) = CreateClient(ValidDraftJson);
        var regen = new RegenerationContext("Съкрати до 200 думи.", "Старият текст на статията.");

        await client.GenerateAsync(Bundle(), regen, CancellationToken.None);

        var userPrompts = fake.LastMessages!.Where(m => m.Role == ChatRole.User).ToList();
        Assert.Equal(2, userPrompts.Count); // bundle block + regeneration block
        Assert.Contains("Редакторът поиска промени", userPrompts[1].Text);
        Assert.Contains("Съкрати до 200 думи.", userPrompts[1].Text);
        Assert.Contains("Предишна версия:", userPrompts[1].Text);
        Assert.Contains("Старият текст на статията.", userPrompts[1].Text);
    }

    [Fact]
    public async Task Generate_tolerates_markdown_fenced_json_and_missing_fields()
    {
        var (client, _, _) = CreateClient(
            """
            ```json
            {"headline": "ЗАГЛАВИЕ", "bodyMarkdown": "Текст."}
            ```
            """);

        var result = await client.GenerateAsync(Bundle(), null, CancellationToken.None);

        // Missing fields become empty values — DraftValidator is the quality gate.
        Assert.Equal("ЗАГЛАВИЕ", result.Content.Headline);
        Assert.Equal("Текст.", result.Content.BodyMarkdown);
        Assert.Null(result.Content.Subtitle);
        Assert.Equal("", result.Content.Category);
        Assert.Empty(result.Content.Tags);
        Assert.Empty(result.Content.ImageSearchQueries);
        Assert.Equal(0, result.Content.Confidence);
        Assert.Equal("", result.Content.FacebookCaption);
        Assert.Empty(result.Content.FacebookHashtags);
        Assert.Equal(0, result.Usage.TokensIn);       // no usage metadata → 0, cost 0
        Assert.Equal(0m, result.Usage.Cost);
    }

    [Fact]
    public async Task Generate_with_malformed_json_throws_a_descriptive_exception()
    {
        var (client, _, _) = CreateClient("Sorry, I cannot write this article.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GenerateAsync(Bundle(), null, CancellationToken.None));

        Assert.Contains("malformed JSON", ex.Message);
        Assert.Contains("Sorry, I cannot write", ex.Message); // payload preview aids debugging
    }

    [Fact]
    public async Task Generate_with_empty_response_throws_AiEmptyResponseException()
    {
        // HTTP 200 with an empty completion (provider load): distinct from malformed JSON so
        // DraftJob classifies it as transient instead of burning the topic's attempt budget.
        var (client, _, _) = CreateClient("");

        var ex = await Assert.ThrowsAsync<AiEmptyResponseException>(
            () => client.GenerateAsync(Bundle(), null, CancellationToken.None));

        Assert.Contains("empty completion", ex.Message);
        Assert.Contains("draft", ex.Message);
    }

    [Fact]
    public async Task Generate_requires_a_non_empty_bundle()
    {
        var (client, _, _) = CreateClient(ValidDraftJson);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.GenerateAsync(new TopicBundle(1, "Празна тема", []), null, CancellationToken.None));
    }

    [Fact]
    public async Task Generate_prompt_asks_for_the_facebook_caption_and_hashtags()
    {
        var (client, fake, _) = CreateClient(ValidDraftJson);

        await client.GenerateAsync(Bundle(), null, CancellationToken.None);

        var systemPrompt = fake.LastMessages!.Single(m => m.Role == ChatRole.System).Text;
        Assert.Contains("facebookCaption", systemPrompt);
        Assert.Contains("facebookHashtags", systemPrompt);
        Assert.Contains("БЕЗ главни букви", systemPrompt); // the hook line must not be ALL CAPS
    }

    [Fact]
    public async Task SelfCheck_parses_unsupported_claims_and_usage()
    {
        var (client, draftFake, selfCheckFake) = CreateClient(ValidDraftJson,
            """{"unsupportedClaims": ["Числото 4.5 не се среща в изворите.", ""]}""",
            usage: new UsageDetails { InputTokenCount = 40, OutputTokenCount = 10 },
            options: new GeminiDraftingOptions { InputPricePerMTok = 1m, OutputPricePerMTok = 2m });

        var result = await client.SelfCheckAsync(Draft(), Bundle(), CancellationToken.None);

        var claim = Assert.Single(result.UnsupportedClaims); // blank entries dropped
        Assert.Equal("Числото 4.5 не се среща в изворите.", claim);
        Assert.Equal(40, result.Usage.TokensIn);
        Assert.Equal(0.00006m, result.Usage.Cost);            // (40*1 + 10*2) / 1M

        // Self-check goes through its own stage client, carrying draft body and sources.
        Assert.Null(draftFake.LastMessages);
        var userPrompt = selfCheckFake.LastMessages!.Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("Земетресение разтърси региона.", userPrompt);
        Assert.Contains("Трус край Симитли", userPrompt);
    }

    [Fact]
    public async Task SelfCheck_with_malformed_json_throws_a_descriptive_exception()
    {
        var (client, _, _) = CreateClient(ValidDraftJson, "The draft looks fine to me.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SelfCheckAsync(Draft(), Bundle(), CancellationToken.None));

        Assert.Contains("malformed JSON", ex.Message);
        Assert.Contains("looks fine to me", ex.Message);
    }

    [Fact]
    public async Task SelfCheck_with_empty_response_throws_AiEmptyResponseException()
    {
        var (client, _, _) = CreateClient(ValidDraftJson, "");

        var ex = await Assert.ThrowsAsync<AiEmptyResponseException>(
            () => client.SelfCheckAsync(Draft(), Bundle(), CancellationToken.None));

        Assert.Contains("empty completion", ex.Message);
        Assert.Contains("self-check", ex.Message);
    }

    /// <summary>Canned-response IChatClient standing in for the Gemini adapter (the ADR-0010 seam).</summary>
    private sealed class FakeChatClient(ChatResponse response) : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            LastOptions = options;
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
