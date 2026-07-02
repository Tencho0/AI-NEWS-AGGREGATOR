namespace Newsroom.Core.Ai;

/// <summary>What one AI request consumed (row in nw_CostLedger). Cost is 0 on the free tier,
/// but the meter stays on so a paid-tier switch is a config change (ADR-0010).</summary>
public sealed record AiUsage(
    string Provider,
    string Model,
    int TokensIn,
    int TokensOut,
    decimal Cost);
