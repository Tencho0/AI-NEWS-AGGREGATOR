using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Core.Images;
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
    AiRateLimiter rateLimiter,
    PublicFigureDirectory? publicFigures = null) : IDraftingAi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PublicFigureDirectory figures = publicFigures ?? PublicFigureDirectory.Empty;

    public async Task<DraftGenerationResult> GenerateAsync(
        TopicBundle bundle, RegenerationContext? regenContext, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bundle.Articles.Count);

        using var lease = await rateLimiter.AcquireAsync(ct).ConfigureAwait(false);

        // Only figures the sources actually name become candidates — the model picks from that
        // list or returns null, so it can never conjure a person the article does not mention.
        var candidates = figures.Mentioned(BundleText(bundle));

        List<ChatMessage> messages =
        [
            new(ChatRole.System, BuildGenerateInstruction(candidates)),
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

    private string BuildGenerateInstruction(IReadOnlyList<PublicFigure> candidates) =>
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
        - "imageScene": ЕДНА свързана сцена на АНГЛИЙСКИ за корицата (1-2 изречения, до 300
          знака) — конкретен момент, който се случва точно сега по темата на статията: какво
          прави главният обект, кои второстепенни елементи го подкрепят, къде е (град, село,
          път, планина, гора, поле, институция, спортна зала — според статията), по кое време на
          деня и при какво време. Пиши го като кадър от репортаж, не като списък с ключови думи;
          без имена на хора, без надписи и текст в кадъра, без празни или статични сцени.
        - "coverHeadline": МНОГО кратко заглавие на БЪЛГАРСКИ за изписване ВЪРХУ корицата — до
          {{CoverTextPlan.MaxHeadlineChars}} знака, 2-5 думи, без точка в края, без кавички.
          Това е текст, който ще бъде нарисуван в самата картинка, затова трябва да се чете от
          един поглед. Не повтаряй дълго заглавието на статията.
        - "coverKeyPoints": 0 до {{CoverTextPlan.MaxKeyPoints}} много кратки акцента на БЪЛГАРСКИ —
          най-важните числа или факти, всеки до {{CoverTextPlan.MaxKeyPointChars}} знака
          (напр. "3 сгради", "18 пожарникари", "12 млн. лв."). Празен масив, ако статията няма
          силно число. Без изречения.
        - "coverTextEmphasis": "headline" ако заглавието трябва да е най-голямото на корицата,
          или "number" ако новината Е числото и то трябва да доминира
        - "coverTextPlacement": едно от: lower-third, lower-left, lower-right, left-third,
          right-third, upper-left — къде да легне текстовият блок, така че да НЕ покрива
          главния обект от "imageScene"
        - "imageAltTextBg": описателен alt текст на български за илюстративната снимка{{PublicFigureBlock(candidates)}}
        - "flaggedClaims": твърдения от изворите, които не можа да провериш или идват само от
          един източник (празен масив, ако няма такива)
        - "confidence": число 0..1 — увереността ти, че статията е точна и пълна
        - "facebookCaption": текст за Facebook пост на български, 400-700 знака. Структура:
          първият ред е кука до 100 знака — нормално изречение, БЕЗ главни букви навсякъде;
          после 1-2 кратки абзаца с основните факти; накрая въпрос или лека подкана към
          читателите (напр. „Разкажете ни в коментарите."). Разговорен, но никога жълт тон.
          Без markdown, без хаштагове и без символите * и # в текста.
        - "facebookHashtags": 2-3 хаштага на български за региона и темата
          (напр. "#Благоевград") — само букви и цифри след #
        """;

    /// <summary>
    /// The <c>imageCentralPerson</c> contract, added only when the sources actually name a
    /// configured public figure (ADR-0012). The model judges visual relevance — is this person
    /// the event, or just a quoted voice — and the image layer still refuses to draw anyone
    /// without an approved reference photo, so a wrong "yes" costs a symbolic cover, never an
    /// invented likeness.
    /// </summary>
    private static string PublicFigureBlock(IReadOnlyList<PublicFigure> candidates)
    {
        if (candidates.Count == 0)
            return "";

        var list = string.Join("; ", candidates.Select(f => $"{f.Name} ({f.Role})"));
        return $"""

        - "imageCentralPerson": едно име ТОЧНО както е изписано в списъка по-долу, или null.
          Избери име САМО ако този човек е наистина централен за събитието — решението,
          изявлението, назначението, появата, кампанията или действието са негови и без него
          новината не съществува. Върни null, ако е само цитиран мимоходом, споменат като фон
          или като част от институция, а също и при криминални, скандални или обвинителни теми,
          където символична сцена е по-уместна от лице. Не измисляй имена извън списъка.
          Разпознати личности в изворите: {list}
        """;
    }

    /// <summary>Everything the sources say, for public-figure mention matching.</summary>
    private static string BundleText(TopicBundle bundle)
    {
        var text = new StringBuilder(bundle.Label);
        foreach (var article in bundle.Articles)
        {
            text.Append('\n').Append(article.Title)
                .Append('\n').Append(article.Summary)
                .Append('\n').Append(article.Text);
        }
        return text.ToString();
    }

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
            CleanList(dto.FacebookHashtags),
            NullIfWhiteSpace(dto.ImageScene),
            NullIfWhiteSpace(dto.ImageCentralPerson),
            CoverTextPlan.From(
                dto.CoverHeadline, dto.CoverKeyPoints, dto.CoverTextPlacement, dto.CoverTextEmphasis));
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
        List<string?>? FacebookHashtags,
        string? ImageScene,
        string? ImageCentralPerson,
        string? CoverHeadline,
        List<string?>? CoverKeyPoints,
        string? CoverTextEmphasis,
        string? CoverTextPlacement);

    /// <summary>Wire shape of the model's self-check JSON object.</summary>
    private sealed record SelfCheckDto(List<string?>? UnsupportedClaims);
}
