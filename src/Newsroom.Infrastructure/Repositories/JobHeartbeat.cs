using Dapper;

using Microsoft.Extensions.Logging;

using Newsroom.Core.Operations;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Repositories;

/// <summary>
/// <see cref="IJobHeartbeat"/> over nw_Config: upserts 'Heartbeat:{job}' = round-trip UTC
/// timestamp (same MERGE shape as the process-level HeartbeatService). Best-effort by
/// contract: a failed beat is logged at debug and swallowed — the watchdog keys on the
/// absence of fresh beats, so failing loudly here would only break the cycle it reports on.
/// </summary>
public sealed class JobHeartbeat(IDbConnectionFactory db, ILogger<JobHeartbeat> logger) : IJobHeartbeat
{
    /// <summary>nw_Config key prefix; the suffix is a <see cref="JobNames"/> constant.</summary>
    public const string KeyPrefix = "Heartbeat:";

    public async Task BeatAsync(string jobName, CancellationToken ct)
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
                new { Key = KeyPrefix + jobName, Value = DateTime.UtcNow.ToString("O") });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Heartbeat for job {Job} failed", jobName);
        }
    }
}
