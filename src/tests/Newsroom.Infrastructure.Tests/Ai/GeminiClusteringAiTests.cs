using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiClusteringAiTests
{
    private static ClusterCandidate Candidate(long id) =>
        new(id, $"Заглавие {id}", "Кратко резюме на статията.", ["Благоевград", "Община Благоевград"]);

    private static ExistingTopicSnapshot Topic(long id, string label) =>
        new(id, label, ["Първо заглавие", "Второ заглавие"]);

    private static (GeminiClusteringAi Client, FakeChatClient Fake) CreateClient(
        string responseText, UsageDetails? usage = null, GeminiClusteringOptions? options = null)
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))
        {
            Usage = usage,
            ModelId = "gemini-2.5-flash",
        };
        var fake = new FakeChatClient(response);
        var client = new GeminiClusteringAi(fake, options ?? new GeminiClusteringOptions(),
            new AiRateLimiter(requestsPerMinute: 1000), // permissive: throttling is not under test
            NullLogger<GeminiClusteringAi>.Instance);
        return (client, fake);
    }

    [Fact]
    public async Task Parses_existing_and_new_assignments_and_usage()
    {
        var (client, fake) = CreateClient(
            """
            [
              {"articleId": 1, "existingTopicId": 10, "newTopicLabel": null},
              {"articleId": 2, "existingTopicId": null, "newTopicLabel": "Наводнение в Петрич"}
            ]
            """,
            new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            new GeminiClusteringOptions { InputPricePerMTok = 1m, OutputPricePerMTok = 2m });

        var result = await client.AssignAsync(
            [Topic(10, "Пожар в Банско")], [Candidate(1), Candidate(2)], CancellationToken.None);

        Assert.Equal(2, result.Assignments.Count);
        Assert.Equal(new ClusterAssignment(1, 10, null), result.Assignments[0]);
        Assert.Equal(new ClusterAssignment(2, null, "Наводнение в Петрич"), result.Assignments[1]);

        Assert.Equal("gemini", result.Usage.Provider);
        Assert.Equal("gemini-2.5-flash", result.Usage.Model);
        Assert.Equal(100, result.Usage.TokensIn);
        Assert.Equal(50, result.Usage.TokensOut);
        Assert.Equal(0.0002m, result.Usage.Cost);     // (100*1 + 50*2) / 1M

        // The provider seam is asked for JSON explicitly, and the prompt carries both sides.
        Assert.Same(ChatResponseFormat.Json, fake.LastOptions?.ResponseFormat);
        var userPrompt = fake.LastMessages!.Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("Пожар в Банско", userPrompt);
        Assert.Contains("Заглавие 2", userPrompt);
    }

    [Fact]
    public async Task Tolerates_markdown_fenced_json()
    {
        var (client, _) = CreateClient(
            """
            ```json
            [{"articleId": 7, "existingTopicId": null, "newTopicLabel": "Протест в Благоевград"}]
            ```
            """);

        var result = await client.AssignAsync([], [Candidate(7)], CancellationToken.None);

        var assignment = Assert.Single(result.Assignments);
        Assert.Equal(7, assignment.ArticleId);
        Assert.Equal("Протест в Благоевград", assignment.NewTopicLabel);
        Assert.Equal(0, result.Usage.TokensIn);       // no usage metadata → 0, cost 0
        Assert.Equal(0m, result.Usage.Cost);
    }

    [Fact]
    public async Task Drops_invalid_rows_and_keeps_the_rest()
    {
        var (client, _) = CreateClient(
            """
            [
              {"articleId": 1, "existingTopicId": null, "newTopicLabel": null},
              {"articleId": 2, "existingTopicId": 10, "newTopicLabel": "И двете"},
              {"articleId": 999, "existingTopicId": 10, "newTopicLabel": null},
              {"articleId": 3, "existingTopicId": 10, "newTopicLabel": null},
              {"articleId": 3, "existingTopicId": null, "newTopicLabel": "Дубликат"}
            ]
            """);

        var result = await client.AssignAsync(
            [Topic(10, "Пожар в Банско")], [Candidate(1), Candidate(2), Candidate(3)],
            CancellationToken.None);

        // Both-null (1), both-set (2), unknown id (999) and the duplicate for 3 are dropped;
        // articles 1 and 2 stay unassigned and are retried next cycle.
        var assignment = Assert.Single(result.Assignments);
        Assert.Equal(new ClusterAssignment(3, 10, null), assignment);
    }

    [Fact]
    public async Task Malformed_json_throws_a_descriptive_exception()
    {
        var (client, _) = CreateClient("Sorry, I cannot cluster these articles.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AssignAsync([], [Candidate(1)], CancellationToken.None));

        Assert.Contains("malformed JSON", ex.Message);
        Assert.Contains("Sorry, I cannot cluster", ex.Message); // payload preview aids debugging
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
