using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Newsroom.Core.Ai;
using Newsroom.Core.Trends;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// <see cref="IClusteringAi"/> on top of the provider-neutral <see cref="IChatClient"/> seam
/// (ADR-0010), mirroring <see cref="GeminiAiClient"/>: one request clusters a whole batch
/// (free-tier quotas are per request) and the shared <see cref="AiRateLimiter"/> keeps the
/// whole process under the free-tier RPM by waiting, never failing.
/// </summary>
public sealed class GeminiClusteringAi(
    IChatClient chatClient,
    GeminiClusteringOptions options,
    AiRateLimiter rateLimiter,
    ILogger<GeminiClusteringAi> logger) : IClusteringAi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClusterBatchResult> AssignAsync(
        IReadOnlyList<ExistingTopicSnapshot> existingTopics,
        IReadOnlyList<ClusterCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfZero(candidates.Count);

        using var lease = await rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instruction),
            new(ChatRole.User, BuildTopicsAndArticlesBlock(existingTopics, candidates)),
        ];
        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            Temperature = 0.2f, // matching stories, not creativity
        };

        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);

        var assignments = ParseAssignments(AiResponseText.RequireCompletion(response, "clustering batch"), candidates);
        var tokensIn = (int)(response.Usage?.InputTokenCount ?? 0);
        var tokensOut = (int)(response.Usage?.OutputTokenCount ?? 0);
        var cost = ((tokensIn * options.InputPricePerMTok) + (tokensOut * options.OutputPricePerMTok)) / 1_000_000m;
        var usage = new AiUsage("gemini", response.ModelId ?? options.Model, tokensIn, tokensOut, cost);

        return new ClusterBatchResult(assignments, usage);
    }

    private const string Instruction =
        """
        You are an editorial assistant at a Bulgarian regional news site covering Blagoevgrad
        and Southwest Bulgaria, grouping news articles into stories. The user message lists
        EXISTING TOPICS (topicId, label, recent article titles) and NEW ARTICLES (articleId,
        title, summary, entities). Assign EVERY new article to exactly one story:
        - an existing topic only when the article covers the SAME concrete story or event —
          not merely the same theme or category;
        - otherwise a NEW topic with a concise Bulgarian label naming the story
          (e.g. "Наводнение в Петрич", not "Времето").
        Respond with ONLY a JSON array of one object per article — no markdown, no commentary:
        {"articleId": n, "existingTopicId": n | null, "newTopicLabel": "..." | null}
        Exactly one of "existingTopicId" and "newTopicLabel" must be non-null.
        """;

    private static string BuildTopicsAndArticlesBlock(
        IReadOnlyList<ExistingTopicSnapshot> existingTopics, IReadOnlyList<ClusterCandidate> candidates)
    {
        var block = new StringBuilder();

        block.Append("EXISTING TOPICS\n\n");
        if (existingTopics.Count == 0)
            block.Append("(none yet)\n\n");
        foreach (var topic in existingTopics)
        {
            block.Append("TopicId: ").Append(topic.TopicId).Append('\n')
                .Append("Label: ").Append(topic.Label).Append('\n')
                .Append("Recent titles: ").AppendJoin(" | ", topic.RecentTitles).Append("\n\n");
        }

        block.Append("NEW ARTICLES\n\n");
        foreach (var candidate in candidates)
        {
            block.Append("ArticleId: ").Append(candidate.ArticleId).Append('\n')
                .Append("Title: ").Append(candidate.Title).Append('\n')
                .Append("Summary: ").Append(candidate.Summary).Append('\n')
                .Append("Entities: ").AppendJoin(", ", candidate.Entities).Append("\n\n");
        }
        return block.ToString();
    }

    private IReadOnlyList<ClusterAssignment> ParseAssignments(
        string text, IReadOnlyList<ClusterCandidate> requested)
    {
        var json = AiResponseText.StripCodeFence(text);

        List<ClusterItemDto>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<ClusterItemDto>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"AI returned malformed JSON for the clustering batch: {AiResponseText.Preview(text)}", ex);
        }
        if (items is null)
            throw new InvalidOperationException(
                $"AI returned no JSON array for the clustering batch: {AiResponseText.Preview(text)}");

        // Ids must come from the request; hallucinated or duplicated ids and rows breaking the
        // exactly-one-of contract are dropped — those articles stay unassigned (retried next cycle).
        var pending = requested.Select(c => c.ArticleId).ToHashSet();
        var assignments = new List<ClusterAssignment>(items.Count);
        foreach (var item in items)
        {
            var label = string.IsNullOrWhiteSpace(item.NewTopicLabel) ? null : item.NewTopicLabel.Trim();
            if ((item.ExistingTopicId is null) == (label is null))
            {
                logger.LogWarning(
                    "AI returned an assignment for article id {ArticleId} without exactly one of existingTopicId/newTopicLabel; dropped",
                    item.ArticleId);
                continue;
            }
            if (!pending.Remove(item.ArticleId))
            {
                logger.LogWarning("AI returned an assignment for unknown or duplicate article id {ArticleId}; dropped",
                    item.ArticleId);
                continue;
            }

            assignments.Add(new ClusterAssignment(item.ArticleId, item.ExistingTopicId, label));
        }
        return assignments;
    }

    /// <summary>Wire shape of one item in the model's JSON array (camelCase, case-insensitive).</summary>
    private sealed record ClusterItemDto(
        long ArticleId,
        long? ExistingTopicId,
        string? NewTopicLabel);
}
