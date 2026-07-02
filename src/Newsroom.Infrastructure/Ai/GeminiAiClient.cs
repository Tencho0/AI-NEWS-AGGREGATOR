using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Newsroom.Core.Ai;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// <see cref="IAiClient"/> on top of the provider-neutral <see cref="IChatClient"/> seam
/// (ADR-0010): the Gemini specifics live in the injected adapter, so tests inject a fake and a
/// provider switch is a different adapter registration. One request analyses a whole batch
/// (free-tier quotas are per request) and a sliding-window throttle keeps us under the
/// free-tier RPM by waiting, never failing.
/// </summary>
public sealed class GeminiAiClient(
    IChatClient chatClient,
    GeminiAiOptions options,
    ILogger<GeminiAiClient> logger) : IAiClient, IDisposable
{
    /// <summary>Per-article text cap in the prompt; keeps a full batch inside sane token limits.</summary>
    internal const int MaxArticleTextChars = 4000;

    private const string FallbackCategory = "Друго";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SlidingWindowRateLimiter rateLimiter = new(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = options.RequestsPerMinute,
        Window = TimeSpan.FromMinutes(1),
        SegmentsPerWindow = 6,
        QueueLimit = int.MaxValue, // throttled callers wait, they are never rejected
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    public async Task<AiBatchResult> SummariseAndClassifyAsync(
        IReadOnlyList<ArticleForAnalysis> articles, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfZero(articles.Count);

        using var lease = await rateLimiter.AcquireAsync(1, ct).ConfigureAwait(false);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, BuildInstruction()),
            new(ChatRole.User, BuildArticlesBlock(articles)),
        ];
        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            Temperature = 0.2f, // classification, not creativity
        };

        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);

        var results = ParseResults(response.Text, articles);
        var tokensIn = (int)(response.Usage?.InputTokenCount ?? 0);
        var tokensOut = (int)(response.Usage?.OutputTokenCount ?? 0);
        var cost = ((tokensIn * options.InputPricePerMTok) + (tokensOut * options.OutputPricePerMTok)) / 1_000_000m;
        var usage = new AiUsage("gemini", response.ModelId ?? options.Model, tokensIn, tokensOut, cost);

        return new AiBatchResult(results, usage);
    }

    public void Dispose() => rateLimiter.Dispose();

    private string BuildInstruction() =>
        $$"""
        You are a journalist's assistant at a Bulgarian regional news site covering Blagoevgrad
        and Southwest Bulgaria. For EVERY article in the user message produce one JSON object:
        - "articleId": the article's Id exactly as given (number)
        - "summary": 2-3 sentences in Bulgarian
        - "category": exactly one of: {{string.Join(", ", options.Categories)}}
        - "regionScore": number 0..1 — relevance to Southwest Bulgaria / Blagoevgrad region
        - "entities": up to 8 names of people, organisations or places mentioned
        - "language": ISO 639-1 code of the language the source article is written in
        - "relevant": false for sports score tables, advertisements, horoscopes and other non-news
        Respond with ONLY a JSON array of these objects — no markdown, no commentary.
        """;

    private static string BuildArticlesBlock(IReadOnlyList<ArticleForAnalysis> articles)
    {
        var block = new StringBuilder();
        for (var i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            var text = article.Text ?? "";
            if (text.Length > MaxArticleTextChars)
                text = text[..MaxArticleTextChars];

            block.Append("Article ").Append(i + 1).Append('\n')
                .Append("Id: ").Append(article.ArticleId).Append('\n')
                .Append("Title: ").Append(article.Title).Append('\n')
                .Append("Source: ").Append(article.SourceName).Append('\n')
                .Append("Date: ")
                .Append(article.PublishedAtUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown")
                .Append('\n')
                .Append("Text:\n").Append(text).Append("\n\n");
        }
        return block.ToString();
    }

    private IReadOnlyList<ArticleAnalysisResult> ParseResults(
        string text, IReadOnlyList<ArticleForAnalysis> requested)
    {
        var json = StripCodeFence(text);

        List<AnalysisItemDto>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<AnalysisItemDto>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"AI returned malformed JSON for the analysis batch: {Preview(text)}", ex);
        }
        if (items is null)
            throw new InvalidOperationException(
                $"AI returned no JSON array for the analysis batch: {Preview(text)}");

        // Ids must come from the request; hallucinated or duplicated ids are dropped and the
        // articles they were meant for stay unanalysed (the job retries them).
        var pending = requested.Select(a => a.ArticleId).ToHashSet();
        var results = new List<ArticleAnalysisResult>(items.Count);
        foreach (var item in items)
        {
            if (!pending.Remove(item.ArticleId))
            {
                logger.LogWarning("AI returned analysis for unknown article id {ArticleId}; dropped", item.ArticleId);
                continue;
            }

            results.Add(new ArticleAnalysisResult(
                item.ArticleId,
                item.Summary?.Trim() ?? "",
                string.IsNullOrWhiteSpace(item.Category) ? FallbackCategory : item.Category.Trim(),
                Math.Clamp(item.RegionScore ?? 0, 0, 1),
                item.Entities?.Where(e => !string.IsNullOrWhiteSpace(e)).Take(8).ToList() ?? [],
                item.Language?.Trim().ToLowerInvariant() ?? "",
                item.Relevant ?? true));
        }
        return results;
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
            return trimmed;

        var body = trimmed[(firstLineEnd + 1)..];
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closingFence >= 0 ? body[..closingFence] : body).Trim();
    }

    private static string Preview(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";

    /// <summary>Wire shape of one item in the model's JSON array (camelCase, case-insensitive).</summary>
    private sealed record AnalysisItemDto(
        long ArticleId,
        string? Summary,
        string? Category,
        double? RegionScore,
        List<string>? Entities,
        string? Language,
        bool? Relevant);
}
