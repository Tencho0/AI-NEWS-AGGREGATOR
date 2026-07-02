using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Newsroom.Core.Scraping;

namespace Newsroom.Infrastructure.Scraping;

/// <summary>
/// Minimal Robots Exclusion Protocol gate: honours Allow/Disallow for our user agent and "*",
/// longest-match-wins, '*' wildcards and '$' anchors supported. Per REP, a missing robots.txt
/// (404) means everything is allowed; fetch errors fail open with a log entry. Verdicts are
/// cached per host for 24 h.
/// </summary>
public sealed class RobotsPolicy(IHttpClientFactory httpClientFactory, ILogger<RobotsPolicy> logger) : IRobotsPolicy
{
    public const string BotToken = "predelnewsbot";
    public const string HttpClientName = "scraper";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public async Task<bool> IsAllowedAsync(Uri url, CancellationToken ct)
    {
        var hostKey = $"{url.Scheme}://{url.Authority}";
        if (!_cache.TryGetValue(hostKey, out var entry) || entry.FetchedAtUtc + CacheTtl < DateTime.UtcNow)
        {
            entry = new CacheEntry(await FetchRulesAsync(hostKey, ct), DateTime.UtcNow);
            _cache[hostKey] = entry;
        }

        return IsAllowed(entry.Rules, url.PathAndQuery);
    }

    private async Task<IReadOnlyList<Rule>> FetchRulesAsync(string hostKey, CancellationToken ct)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.GetAsync($"{hostKey}/robots.txt", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return [];
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("robots.txt at {Host} returned {Status}; failing open", hostKey, response.StatusCode);
                return [];
            }

            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "robots.txt fetch failed for {Host}; failing open", hostKey);
            return [];
        }
    }

    /// <summary>Parses robots.txt, keeping the most specific applicable group (our token beats "*").</summary>
    internal static IReadOnlyList<Rule> Parse(string content)
    {
        List<Rule> starRules = [], botRules = [];
        List<Rule>? currentTargets = null;
        var currentGroupIsBot = false;
        var previousLineWasUserAgent = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split(':', 2);
            if (parts.Length != 2)
                continue;

            var field = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();

            switch (field)
            {
                case "user-agent":
                    if (!previousLineWasUserAgent)
                    {
                        currentTargets = null;
                        currentGroupIsBot = false;
                    }

                    if (value.Contains(BotToken, StringComparison.OrdinalIgnoreCase))
                    {
                        currentTargets = botRules;
                        currentGroupIsBot = true;
                    }
                    else if (value == "*" && !currentGroupIsBot)
                    {
                        currentTargets = starRules;
                    }

                    previousLineWasUserAgent = true;
                    continue;

                case "allow" or "disallow" when currentTargets is not null && value.Length > 0:
                    currentTargets.Add(new Rule(value, Allow: field == "allow"));
                    break;
            }

            previousLineWasUserAgent = false;
        }

        return botRules.Count > 0 ? botRules : starRules;
    }

    /// <summary>Longest-pattern match wins; tie goes to Allow; no match means allowed.</summary>
    internal static bool IsAllowed(IReadOnlyList<Rule> rules, string pathAndQuery)
    {
        Rule? winner = null;
        foreach (var rule in rules)
        {
            if (!Matches(rule.Pattern, pathAndQuery))
                continue;
            if (winner is null
                || rule.Pattern.Length > winner.Pattern.Length
                || (rule.Pattern.Length == winner.Pattern.Length && rule.Allow && !winner.Allow))
            {
                winner = rule;
            }
        }

        return winner?.Allow ?? true;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        var segments = (anchored ? pattern[..^1] : pattern).Split('*');

        var position = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
                continue;

            var index = i == 0
                ? (path.StartsWith(segment, StringComparison.Ordinal) ? 0 : -1)
                : path.IndexOf(segment, position, StringComparison.Ordinal);
            if (index < 0)
                return false;
            position = index + segment.Length;
        }

        return !anchored || position == path.Length || segments[^1].Length == 0;
    }

    internal sealed record Rule(string Pattern, bool Allow);

    private sealed record CacheEntry(IReadOnlyList<Rule> Rules, DateTime FetchedAtUtc);
}
