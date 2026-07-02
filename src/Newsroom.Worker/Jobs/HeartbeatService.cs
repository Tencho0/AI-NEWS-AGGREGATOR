using Dapper;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Worker.Jobs;

/// <summary>
/// Logs a heartbeat and records it in nw_Config on an interval. This is the watchdog signal
/// the ops alerts key on (docs/07-operations.md) and it exercises the full DB round trip.
/// </summary>
public sealed class HeartbeatService(
    IDbConnectionFactory db,
    IConfiguration configuration,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    public const string ConfigKey = "Worker:LastHeartbeatUtc";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Worker:HeartbeatSeconds", 60));
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                await BeatAsync(interval, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task BeatAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var connection = await db.OpenAsync(ct);
            await connection.ExecuteAsync(
                """
                MERGE dbo.nw_Config AS target
                USING (SELECT @Key AS [Key]) AS source ON target.[Key] = source.[Key]
                WHEN MATCHED THEN UPDATE SET [Value] = @Value, UpdatedAtUtc = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT ([Key], [Value]) VALUES (@Key, @Value);
                """,
                new { Key = ConfigKey, Value = DateTime.UtcNow.ToString("O") });

            logger.LogInformation("Heartbeat OK (interval {Interval})", interval);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Heartbeat failure is a symptom, not a reason to die — the watchdog alerting
            // (Phase 7) keys on the absence of fresh heartbeats.
            logger.LogWarning(ex, "Heartbeat failed");
        }
    }
}
