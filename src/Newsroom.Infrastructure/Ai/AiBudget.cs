using Dapper;

using Microsoft.Extensions.Configuration;

using Newsroom.Core.Ai;
using Newsroom.Infrastructure.Database;

namespace Newsroom.Infrastructure.Ai;

/// <summary>
/// Daily request budget over nw_CostLedger (ADR-0010: free-tier quotas, not dollars, are the
/// binding constraint). The per-stage cap comes from <c>Ai:Stages:{stage}:DailyRequestBudget</c>
/// (default 1000). Reserve/record are two steps, so parallel stages could slightly overshoot —
/// acceptable at our request volume.
/// </summary>
public sealed class AiBudget(IDbConnectionFactory db, IConfiguration configuration) : IAiBudget
{
    public async Task<bool> TryReserveAsync(string stage, CancellationToken ct)
    {
        var budget = configuration.GetValue($"Ai:Stages:{stage}:DailyRequestBudget", 1000);

        using var connection = await db.OpenAsync(ct);
        var usedToday = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM dbo.nw_CostLedger
            WHERE Stage = @stage AND AtUtc >= @todayUtc
            """,
            new { stage, todayUtc = DateTime.UtcNow.Date });
        return usedToday < budget;
    }

    public async Task RecordAsync(string stage, AiUsage usage, CancellationToken ct)
    {
        using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO dbo.nw_CostLedger (Stage, Provider, Model, RequestCount, TokensIn, TokensOut, Cost)
            VALUES (@stage, @Provider, @Model, 1, @TokensIn, @TokensOut, @Cost)
            """,
            new { stage, usage.Provider, usage.Model, usage.TokensIn, usage.TokensOut, usage.Cost });
    }
}
