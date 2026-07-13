using Microsoft.Extensions.Configuration;

using Newsroom.Core.Operations;
using Newsroom.Infrastructure.Publishing;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Infrastructure.Operations;

/// <summary>
/// Per-job heartbeat allowance = 3× the interval each job configures itself with
/// (docs/07-operations.md). Shared by <c>WatchdogJob</c> (which pages when a beat exceeds its
/// allowance) and the <c>/health</c> command (which shows the same staleness view), so the
/// diagnostic and the pager can never disagree. Telegram and Publish are listed only when
/// configured — unconfigured jobs stay dormant and must not read as stale.
/// </summary>
public static class JobStalenessPolicy
{
    public static IReadOnlyList<(string JobName, TimeSpan Allowance)> BuildExpectations(
        IConfiguration configuration)
    {
        var expectations = new List<(string, TimeSpan)>
        {
            (JobNames.Scrape, Allowance(configuration, "Scrape:CheckSeconds", 60)),
            (JobNames.Analyse, Allowance(configuration, "Ai:Stages:Analyse:CheckSeconds", 120)),
            (JobNames.Trend, Allowance(configuration, "Ai:Stages:Cluster:CheckSeconds", 300)),
            (JobNames.Draft, Allowance(configuration, "Ai:Stages:Draft:CheckSeconds", 300)),
        };

        var telegram = TelegramOptions.From(configuration);
        if (telegram.IsConfigured)
            expectations.Add((JobNames.Telegram,
                TimeSpan.FromSeconds(3 * telegram.PollTimeoutSeconds + 60)));

        var umbraco = UmbracoOptions.From(configuration);
        if (umbraco.IsConfigured)
            expectations.Add((JobNames.Publish, TimeSpan.FromSeconds(3 * umbraco.CheckSeconds)));

        return expectations;
    }

    private static TimeSpan Allowance(IConfiguration configuration, string intervalKey, int defaultSeconds) =>
        TimeSpan.FromSeconds(3 * configuration.GetValue(intervalKey, defaultSeconds));
}
