using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Core.Prompts;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// <see cref="IDraftingAi"/> on top of the provider-neutral <see cref="IChatClient"/> seam
/// (ADR-0010), mirroring <see cref="GeminiAiClient"/>: two clients because model choice is
/// per stage (<c>Draft</c> writes, <c>SelfCheck</c> verifies — the verifier can be cheaper),
/// and the shared <see cref="AiRateLimiter"/> keeps the whole process under the free-tier RPM
/// by waiting, never failing. The system prompt embeds the editorial style guide
/// (<see cref="PromptLibrary.EditorialStyleGuide"/>), single-sourced from docs/.
/// </summary>
public sealed class GeminiDraftingAi(
    IChatClient draftChatClient,
    IChatClient selfCheckChatClient,
    GeminiDraftingOptions options,
    AiRateLimiter rateLimiter) : IDraftingAi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DraftGenerationResult> GenerateAsync(
        TopicBundle bundle, RegenerationContext? regenContext, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bundle.Articles.Count);

        using var lease = await rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, BuildGenerateInstruction()),
            new(ChatRole.User, BuildBundleBlock(bundle)),
        ];
        if (regenContext is not null)
            messages.Add(new ChatMessage(ChatRole.User, BuildRegenerationBlock(regenContext)));
        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            Temperature = 0.6f, // journalism: some voice, no invention — the prompt forbids new facts
        };

        var response = await draftChatClient.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);

        var content = ParseDraft(AiResponseText.RequireCompletion(response, "draft"));
        return new DraftGenerationResult(content, UsageFrom(response, options.DraftModel));
    }

    public async Task<SelfCheckResult> SelfCheckAsync(
        DraftContent draft, TopicBundle bundle, CancellationToken ct)
    {
        using var lease = await rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, SelfCheckInstruction),
            new(ChatRole.User, BuildSelfCheckBlock(draft, bundle)),
        ];
        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            Temperature = 0.1f, // verification, not creativity
        };

        var response = await selfCheckChatClient.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);

        var claims = ParseSelfCheck(AiResponseText.RequireCompletion(response, "self-check"));
        return new SelfCheckResult(claims, UsageFrom(response, options.SelfCheckModel));
    }

    private AiUsage UsageFrom(ChatResponse response, string fallbackModel)
    {
        var tokensIn = (int)(response.Usage?.InputTokenCount ?? 0);
        var tokensOut = (int)(response.Usage?.OutputTokenCount ?? 0);
        var cost = ((tokensIn * options.InputPricePerMTok) + (tokensOut * options.OutputPricePerMTok)) / 1_000_000m;
        return new AiUsage("gemini", response.ModelId ?? fallbackModel, tokensIn, tokensOut, cost);
    }

    private string BuildGenerateInstruction() =>
        $$"""
        Ти си журналист в регионалната медия Predel News.

        {{PromptLibrary.EditorialStyleGuide}}

        ### Твърди правила
        - Пиши ЕДИНСТВЕНО въз основа на изворите в съобщението на потребителя.
        - Оригинален синтез — никога превод или копие на текста на един източник.
        - Посочвай източници в текста („съобщи БТА", „според общинската администрация").
        - При противоречие между източниците го отбележи изрично.
        - При малко информация — кратка статия; не разтягай.
        - Никакви измислени факти, цитати или числа.

        Върни САМО един JSON обект — без markdown, без коментар — с точно тези полета:
        - "headline": заглавие С ГЛАВНИ БУКВИ по правилата по-горе
        - "subtitle": подзаглавие (или null)
        - "bodyMarkdown": текстът на статията в Markdown
        - "category": точно една от: {{string.Join(", ", options.Categories)}}
        - "region": една от: {{string.Join(", ", options.Regions)}} — или null, ако никоя не подхожда
        - "tags": до 6 тага на български
        - "seoTitle": до 70 знака
        - "seoDescription": до 160 знака
        - "imageSearchQueries": 2-3 заявки за стокови снимки на АНГЛИЙСКИ — общи и илюстративни,
          без имена на хора
        - "imageAltTextBg": описателен alt текст на български за илюстративната снимка
        - "flaggedClaims": твърдения от изворите, които не можа да провериш или идват само от
          един източник (празен масив, ако няма такива)
        - "confidence": число 0..1 — увереността ти, че статията е точна и пълна
        """;

    private const string SelfCheckInstruction =
        """
        Ти проверяваш черновата на новинарска статия срещу нейните извори. Изброй твърденията
        от ЧЕРНОВАТА (факти, числа, имена, цитати), които НЕ се подкрепят от нито един от
        ИЗВОРИТЕ. Върни САМО JSON обект — без markdown, без коментар:
        {"unsupportedClaims": ["...", "..."]}
        Ако всички твърдения са подкрепени, върни {"unsupportedClaims": []}.
        """;

    private static string BuildBundleBlock(TopicBundle bundle)
    {
        var block = new StringBuilder();
        block.Append("ТЕМА: ").Append(bundle.Label).Append("\n\n");
        block.Append("ИЗВОРИ\n\n");
        AppendArticles(block, bundle);
        block.Append("Целева дължина: 250-450 думи.\n");
        return block.ToString();
    }

    /// <summary>Extra user-content block for ✏️ Промени: the editor's instructions win over the
    /// defaults, and the previous version anchors what "промени" is relative to.</summary>
    private static string BuildRegenerationBlock(RegenerationContext regenContext)
    {
        var block = new StringBuilder();
        block.Append("Редакторът поиска промени по предишната версия. Инструкции: ")
            .Append(regenContext.Instructions).Append('\n');
        if (!string.IsNullOrWhiteSpace(regenContext.PreviousBody))
            block.Append('\n').Append("Предишна версия:\n").Append(regenContext.PreviousBody).Append('\n');
        return block.ToString();
    }

    private static string BuildSelfCheckBlock(DraftContent draft, TopicBundle bundle)
    {
        var block = new StringBuilder();
        block.Append("ЧЕРНОВА\n\n").Append(draft.Headline).Append("\n\n")
            .Append(draft.BodyMarkdown).Append("\n\n");
        block.Append("ИЗВОРИ\n\n");
        AppendArticles(block, bundle);
        return block.ToString();
    }

    private static void AppendArticles(StringBuilder block, TopicBundle bundle)
    {
        for (var i = 0; i < bundle.Articles.Count; i++)
        {
            var article = bundle.Articles[i];
            block.Append("Извор ").Append(i + 1).Append('\n')
                .Append("Заглавие: ").Append(article.Title).Append('\n')
                .Append("Медия: ").Append(article.SourceName).Append('\n')
                .Append("Дата: ")
                .Append(article.PublishedAtUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "неизвестна")
                .Append('\n')
                .Append("Резюме: ").Append(article.Summary).Append('\n');
            if (!string.IsNullOrWhiteSpace(article.Text))
                block.Append("Текст:\n").Append(article.Text).Append('\n');
            block.Append('\n');
        }
    }

    private static DraftContent ParseDraft(string text)
    {
        var json = AiResponseText.StripCodeFence(text);

        DraftDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DraftDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"AI returned malformed JSON for the draft: {AiResponseText.Preview(text)}", ex);
        }
        if (dto is null)
            throw new InvalidOperationException(
                $"AI returned no JSON object for the draft: {AiResponseText.Preview(text)}");

        // Missing fields map to empty values, not exceptions: DraftValidator is the quality
        // gate and produces editor-readable violations instead of a stack trace.
        return new DraftContent(
            dto.Headline?.Trim() ?? "",
            NullIfWhiteSpace(dto.Subtitle),
            dto.BodyMarkdown?.Trim() ?? "",
            dto.Category?.Trim() ?? "",
            NullIfWhiteSpace(dto.Region),
            CleanList(dto.Tags),
            dto.SeoTitle?.Trim() ?? "",
            dto.SeoDescription?.Trim() ?? "",
            CleanList(dto.ImageSearchQueries),
            NullIfWhiteSpace(dto.ImageAltTextBg),
            CleanList(dto.FlaggedClaims),
            dto.Confidence ?? 0,
            dto.FacebookCaption?.Trim() ?? "",
            CleanList(dto.FacebookHashtags));
    }

    private static IReadOnlyList<string> ParseSelfCheck(string text)
    {
        var json = AiResponseText.StripCodeFence(text);

        SelfCheckDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SelfCheckDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"AI returned malformed JSON for the self-check: {AiResponseText.Preview(text)}", ex);
        }
        if (dto is null)
            throw new InvalidOperationException(
                $"AI returned no JSON object for the self-check: {AiResponseText.Preview(text)}");

        return CleanList(dto.UnsupportedClaims);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> CleanList(List<string?>? values) =>
        values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList() ?? [];

    /// <summary>Wire shape of the model's draft JSON object (camelCase, case-insensitive).</summary>
    private sealed record DraftDto(
        string? Headline,
        string? Subtitle,
        string? BodyMarkdown,
        string? Category,
        string? Region,
        List<string?>? Tags,
        string? SeoTitle,
        string? SeoDescription,
        List<string?>? ImageSearchQueries,
        string? ImageAltTextBg,
        List<string?>? FlaggedClaims,
        double? Confidence,
        string? FacebookCaption,
        List<string?>? FacebookHashtags);

    /// <summary>Wire shape of the model's self-check JSON object.</summary>
    private sealed record SelfCheckDto(List<string?>? UnsupportedClaims);
}
