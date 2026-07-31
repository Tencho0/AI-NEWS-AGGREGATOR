using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Newsroom.Core.Operations;
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Infrastructure.Operations;

/// <summary>
/// <see cref="IOperatorAlerts"/> over the review chat, following <c>WatchdogJob</c>'s pattern:
/// the gateway is <see cref="Lazy{T}"/> so an unconfigured bot token degrades to a log line
/// instead of failing host startup, and a failed send is logged rather than thrown — an alert
/// must never break the pipeline it is reporting on.
/// </summary>
public sealed class TelegramOperatorAlerts(
    Lazy<ITelegramGateway> gateway,
    IConfiguration configuration,
    ILogger<TelegramOperatorAlerts> logger) : IOperatorAlerts
{
    public async Task RaiseAsync(string message, CancellationToken ct)
    {
        var telegram = TelegramOptions.From(configuration);
        if (!telegram.IsConfigured)
        {
            logger.LogWarning("Operator alert not sent (Telegram is not configured): {Message}", message);
            return;
        }

        try
        {
            await gateway.Value.SendHtmlAsync(
                telegram.ReviewChatId,
                ReviewMessageRenderer.Escape(message),
                withReviewButtons: false, draftIdForButtons: null, scheduleButtonLabel: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the operator alert: {Message}", message);
        }
    }
}
