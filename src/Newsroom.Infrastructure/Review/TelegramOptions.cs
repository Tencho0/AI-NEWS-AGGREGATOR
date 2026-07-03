using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Review;

/// <summary>
/// Settings for the Telegram review loop, bound from configuration: <c>Telegram:BotToken</c>
/// (fallback: the <c>TELEGRAM_BOT_TOKEN</c> environment variable — secrets are not configuration,
/// docs/06-security.md), <c>Telegram:ReviewChatId</c>, <c>Telegram:AllowedUserIds</c> (the editor
/// allowlist = the authorization model, docs/05-integrations/telegram.md), plus the review TTL,
/// long-poll timeout and dispatch batch size.
/// </summary>
public sealed record TelegramOptions
{
    public string? BotToken { get; init; }
    public long ReviewChatId { get; init; }
    public IReadOnlyList<long> AllowedUserIds { get; init; } = [];
    public int ReviewTtlHours { get; init; } = 12;
    public int PollTimeoutSeconds { get; init; } = 25;
    public int MaxSendPerCycle { get; init; } = 5;

    public bool HasToken => !string.IsNullOrWhiteSpace(BotToken);

    /// <summary>The loop only runs fully configured: token, review chat and at least one editor.</summary>
    public bool IsConfigured => HasToken && ReviewChatId != 0 && AllowedUserIds.Count > 0;

    public static TelegramOptions From(IConfiguration configuration) => new()
    {
        BotToken = ResolveBotToken(configuration),
        ReviewChatId = configuration.GetValue("Telegram:ReviewChatId", 0L),
        AllowedUserIds = configuration.GetSection("Telegram:AllowedUserIds").Get<long[]>() ?? [],
        ReviewTtlHours = configuration.GetValue("Telegram:ReviewTtlHours", 12),
        PollTimeoutSeconds = configuration.GetValue("Telegram:PollTimeoutSeconds", 25),
        MaxSendPerCycle = configuration.GetValue("Telegram:MaxSendPerCycle", 5),
    };

    private static string? ResolveBotToken(IConfiguration configuration)
    {
        var configured = configuration.GetValue<string?>("Telegram:BotToken", null);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var environment = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        return string.IsNullOrWhiteSpace(environment) ? null : environment;
    }
}
