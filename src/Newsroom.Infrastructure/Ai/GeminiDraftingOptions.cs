using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Settings for <see cref="GeminiDraftingAi"/>, bound from configuration:
/// <c>Ai:Stages:Draft:Model</c> / <c>Ai:Stages:SelfCheck:Model</c> (model ids are config, not
/// code — ADR-0010), <c>Ai:Categories</c> and <c>Ai:Regions</c> (the site taxonomy the prompt
/// offers and the validator enforces), <c>Ai:Prices:*</c> (USD per million tokens; 0 = free
/// tier). Cadence, budgets and bundle sizes are read by DraftJob/AiBudget from
/// <c>Ai:Stages:Draft:*</c>; the RPM throttle lives in the shared <see cref="AiRateLimiter"/>.
/// </summary>
public sealed record GeminiDraftingOptions
{
    public string DraftModel { get; init; } = "gemini-2.5-flash";
    public string SelfCheckModel { get; init; } = "gemini-2.5-flash";
    public IReadOnlyList<string> Categories { get; init; } = GeminiAiOptions.DefaultCategories;
    public IReadOnlyList<string> Regions { get; init; } = DefaultRegions;
    public decimal InputPricePerMTok { get; init; }
    public decimal OutputPricePerMTok { get; init; }

    /// <summary>The municipalities of the Blagoevgrad district — the site's region taxonomy.</summary>
    public static readonly IReadOnlyList<string> DefaultRegions =
    [
        "Благоевград", "Петрич", "Сандански", "Гоце Делчев", "Разлог", "Банско", "Симитли",
        "Кресна", "Струмяни", "Якоруда", "Белица", "Хаджидимово", "Гърмен", "Сатовча",
    ];

    public static GeminiDraftingOptions From(IConfiguration configuration) => new()
    {
        DraftModel = configuration.GetValue("Ai:Stages:Draft:Model", "gemini-2.5-flash")!,
        SelfCheckModel = configuration.GetValue("Ai:Stages:SelfCheck:Model", "gemini-2.5-flash")!,
        Categories = configuration.GetSection("Ai:Categories").Get<string[]>() is { Length: > 0 } categories
            ? categories
            : GeminiAiOptions.DefaultCategories,
        Regions = configuration.GetSection("Ai:Regions").Get<string[]>() is { Length: > 0 } regions
            ? regions
            : DefaultRegions,
        InputPricePerMTok = configuration.GetValue("Ai:Prices:InputPerMTok", 0m),
        OutputPricePerMTok = configuration.GetValue("Ai:Prices:OutputPerMTok", 0m),
    };
}
