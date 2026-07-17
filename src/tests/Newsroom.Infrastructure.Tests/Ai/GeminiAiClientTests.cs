using BlockedReason = Google.GenAI.Types.BlockedReason;
using GenerateContentResponse = Google.GenAI.Types.GenerateContentResponse;
using GenerateContentResponsePromptFeedback = Google.GenAI.Types.GenerateContentResponsePromptFeedback;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Newsroom.Core.Ai;
using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiAiClientTests
{
    private static ArticleForAnalysis Article(long id, string? text = "Достатъчно дълъг текст за анализ.") =>
        new(id, $"Заглавие {id}", text, "Тестов източник", new DateTime(2026, 7, 1));

    private static (GeminiAiClient Client, FakeChatClient Fake) CreateClient(
        string responseText, UsageDetails? usage = null, GeminiAiOptions? options = null,
        ChatFinishReason? finishReason = null, object? rawRepresentation = null)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))
        {
            Usage = usage,
            ModelId = "gemini-2.5-flash",
            FinishReason = finishReason,
            RawRepresentation = rawRepresentation,
        };
        var fake = new FakeChatClient(response);
        var client = new GeminiAiClient(fake, options ?? new GeminiAiOptions(),
            new AiRateLimiter(requestsPerMinute: 1000), // permissive: throttling is not under test
            NullLogger<GeminiAiClient>.Instance);
        return (client, fake);
    }

    [Fact]
    public async Task Parses_results_and_usage_from_a_clean_json_array()
    {
        var (client, fake) = CreateClient(
            """
            [
              {"articleId": 1, "summary": "Кратко резюме.", "category": "Общество",
               "regionScore": 0.9, "entities": ["Благоевград", "Община Благоевград"],
               "language": "bg", "relevant": true},
              {"articleId": 2, "summary": "Second summary.", "category": "Спорт",
               "regionScore": 1.5, "entities": ["a","b","c","d","e","f","g","h","i"],
               "language": "EN", "relevant": false}
            ]
            """,
            new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            new GeminiAiOptions { InputPricePerMTok = 1m, OutputPricePerMTok = 2m });

        var result = await client.SummariseAndClassifyAsync([Article(1), Article(2)], CancellationToken.None);

        Assert.Equal(2, result.Results.Count);
        var first = result.Results[0];
        Assert.Equal(1, first.ArticleId);
        Assert.Equal("Кратко резюме.", first.Summary);
        Assert.Equal("Общество", first.Category);
        Assert.Equal(0.9, first.RegionScore);
        Assert.Equal(["Благоевград", "Община Благоевград"], first.Entities);
        Assert.Equal("bg", first.Language);
        Assert.True(first.Relevant);

        var second = result.Results[1];
        Assert.Equal(1.0, second.RegionScore);        // clamped to 0..1
        Assert.Equal(8, second.Entities.Count);       // capped at 8
        Assert.Equal("en", second.Language);          // normalised to lowercase
        Assert.False(second.Relevant);

        Assert.Equal("gemini", result.Usage.Provider);
        Assert.Equal("gemini-2.5-flash", result.Usage.Model);
        Assert.Equal(100, result.Usage.TokensIn);
        Assert.Equal(50, result.Usage.TokensOut);
        Assert.Equal(0.0002m, result.Usage.Cost);     // (100*1 + 50*2) / 1M

        // The provider seam is asked for JSON explicitly.
        Assert.Same(ChatResponseFormat.Json, fake.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task Tolerates_markdown_fenced_json()
    {
        var (client, _) = CreateClient(
            """
            ```json
            [{"articleId": 7, "summary": "Резюме.", "category": "Политика",
              "regionScore": 0.5, "entities": [], "language": "bg", "relevant": true}]
            ```
            """);

        var result = await client.SummariseAndClassifyAsync([Article(7)], CancellationToken.None);

        var analysis = Assert.Single(result.Results);
        Assert.Equal(7, analysis.ArticleId);
        Assert.Equal("Резюме.", analysis.Summary);
        Assert.Equal(0, result.Usage.TokensIn);       // no usage metadata → 0, cost 0
        Assert.Equal(0m, result.Usage.Cost);
    }

    [Fact]
    public async Task Drops_unknown_article_ids_and_keeps_the_rest()
    {
        var (client, _) = CreateClient(
            """
            [
              {"articleId": 1, "summary": "Резюме.", "category": "Общество",
               "regionScore": 0.4, "entities": [], "language": "bg", "relevant": true},
              {"articleId": 999, "summary": "Халюцинация.", "category": "Друго",
               "regionScore": 0, "entities": [], "language": "bg", "relevant": true}
            ]
            """);

        var result = await client.SummariseAndClassifyAsync([Article(1), Article(2)], CancellationToken.None);

        var analysis = Assert.Single(result.Results); // 999 dropped, 2 missing → stays unanalysed
        Assert.Equal(1, analysis.ArticleId);
    }

    [Fact]
    public async Task Malformed_json_throws_a_descriptive_exception()
    {
        var (client, _) = CreateClient("Sorry, I cannot analyse these articles.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SummariseAndClassifyAsync([Article(1)], CancellationToken.None));

        Assert.Contains("malformed JSON", ex.Message);
        Assert.Contains("Sorry, I cannot analyse", ex.Message); // payload preview aids debugging
    }

    [Fact]
    public async Task Empty_response_throws_AiEmptyResponseException_with_finish_reason()
    {
        // HTTP 200 with an empty completion must be distinguishable from malformed JSON, and the
        // finish reason must be carried so AiTransientErrors can classify it (content_filter →
        // permanent, burns the attempt; load-shaped → transient, retried).
        var (client, _) = CreateClient("", finishReason: ChatFinishReason.ContentFilter);

        var ex = await Assert.ThrowsAsync<AiEmptyResponseException>(
            () => client.SummariseAndClassifyAsync([Article(1)], CancellationToken.None));

        Assert.Contains("empty completion", ex.Message);
        Assert.Equal("content_filter", ex.FinishReason); // diagnosable, e.g. a SAFETY block
    }

    [Fact]
    public async Task Prompt_blocked_empty_response_carries_the_block_reason()
    {
        // Gemini can refuse the whole prompt (promptFeedback.blockReason, e.g. PROHIBITED_CONTENT):
        // HTTP 200, no candidates, so the adapter yields empty text and a null finish reason.
        // Unlike a load-shaped empty this is deterministic for the same batch (the 2026-07-16
        // Analyse stall), so the block reason must be carried for classification and the log.
        var raw = new GenerateContentResponse
        {
            PromptFeedback = new GenerateContentResponsePromptFeedback
            {
                BlockReason = BlockedReason.ProhibitedContent,
            },
        };
        var (client, _) = CreateClient("", rawRepresentation: raw);

        var ex = await Assert.ThrowsAsync<AiEmptyResponseException>(
            () => client.SummariseAndClassifyAsync([Article(1)], CancellationToken.None));

        Assert.Equal("PROHIBITED_CONTENT", ex.BlockReason);
        Assert.Contains("PROHIBITED_CONTENT", ex.Message); // the real reason, not "unknown"
    }

    [Fact]
    public async Task Empty_response_without_prompt_feedback_has_no_block_reason()
    {
        // A load-shaped empty (raw response present but no promptFeedback) must stay
        // distinguishable from a block, so it keeps its transient classification.
        var (client, _) = CreateClient("", rawRepresentation: new GenerateContentResponse());

        var ex = await Assert.ThrowsAsync<AiEmptyResponseException>(
            () => client.SummariseAndClassifyAsync([Article(1)], CancellationToken.None));

        Assert.Null(ex.BlockReason);
    }

    [Fact]
    public async Task Whitespace_only_response_counts_as_empty()
    {
        var (client, _) = CreateClient(" \n\t ");

        var ex = await Assert.ThrowsAsync<AiEmptyResponseException>(
            () => client.SummariseAndClassifyAsync([Article(1)], CancellationToken.None));

        Assert.Null(ex.FinishReason);
        Assert.Contains("unknown", ex.Message); // no finish reason supplied by the provider
    }

    [Fact]
    public async Task Long_article_text_is_truncated_in_the_prompt()
    {
        var (client, fake) = CreateClient(
            """
            [{"articleId": 1, "summary": "Резюме.", "category": "Общество",
              "regionScore": 0.1, "entities": [], "language": "bg", "relevant": true}]
            """);
        var text = new string('x', GeminiAiClient.MaxArticleTextChars) + "OVERFLOW";

        await client.SummariseAndClassifyAsync([Article(1, text)], CancellationToken.None);

        var userPrompt = fake.LastMessages!.Single(m => m.Role == ChatRole.User).Text;
        Assert.DoesNotContain("OVERFLOW", userPrompt);
        Assert.Contains(new string('x', GeminiAiClient.MaxArticleTextChars), userPrompt);
        Assert.InRange(userPrompt.Length, 1, GeminiAiClient.MaxArticleTextChars + 500);
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
