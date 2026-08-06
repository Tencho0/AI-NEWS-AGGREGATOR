namespace Newsroom.Core.Publishing;

/// <summary>
/// The whole publish-routing table in one pure place: which drafts each publishing leg selects,
/// and what "fully Published" means for a given draft. PublishJob and PublishRepository read it
/// instead of each re-deriving the rules, and because it is pure it is also the only part of the
/// routing that can be unit-tested — the repository queries have no DB test harness
/// (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md).
/// </summary>
public static class PublishTargets
{
    /// <summary>Trailing segment of the target buttons' callback data,
    /// "approve:{draftId}:{token}". Bare "approve:{draftId}" carries no token and means
    /// <see cref="PublishTarget.Both"/> — that is what keeps cards posted before this feature,
    /// and the scheduled card's „✅ Одобри веднага" button, working unchanged.</summary>
    public const string WebsiteToken = "site";

    public const string FacebookToken = "fb";

    public static bool TryParseCallbackToken(string token, out PublishTarget target)
    {
        switch (token)
        {
            case WebsiteToken:
                target = PublishTarget.Website;
                return true;
            case FacebookToken:
                target = PublishTarget.Facebook;
                return true;
            default:
                target = PublishTarget.Both;
                return false;
        }
    }

    /// <summary>The value written to nw_Draft.PublishTarget.</summary>
    public static string Name(PublishTarget target) => target.ToString();

    /// <summary>Reads a persisted nw_Draft.PublishTarget value. Anything unrecognised — NULL from
    /// a pre-migration row read by an old query, a hand-edited value — reads as
    /// <see cref="PublishTarget.Both"/>, the pre-feature behaviour, so a bad row degrades to the
    /// old flow rather than stranding a draft in a leg that never selects it.</summary>
    public static PublishTarget Parse(string? persisted) =>
        Enum.TryParse<PublishTarget>(persisted, ignoreCase: false, out var target)
            ? target
            : PublishTarget.Both;

    /// <summary>Targets the Umbraco leg publishes; Facebook-only drafts never touch the site.</summary>
    public static IReadOnlyList<string> UmbracoLeg { get; } =
        [nameof(PublishTarget.Both), nameof(PublishTarget.Website)];

    /// <summary>Targets served by the "link post after the site publish" Facebook leg
    /// (IPublishRepository.GetPendingFacebookAsync) — the unchanged normal pipeline.</summary>
    public static IReadOnlyList<string> FacebookLinkLeg { get; } = [nameof(PublishTarget.Both)];

    /// <summary>Targets served by the standalone (no link) Facebook leg
    /// (IPublishRepository.GetApprovedForFacebookAsync). Under <c>Publishing:FacebookOnly</c> the
    /// flag widens the column to also admit Both — otherwise a draft approved as Both, or
    /// scheduled with 📅 (which always writes Both), would wait forever for a site publish the
    /// flag has disabled. Website is deliberately never admitted here, flag or no flag: 🌐 Само
    /// сайт is the editor's explicit "not Facebook", and a Website draft has nowhere legitimate
    /// to go while the site leg is off — it waits for the flag to clear rather than being posted
    /// to the page anyway.</summary>
    public static IReadOnlyList<string> FacebookStandaloneLeg(bool facebookOnly) => facebookOnly
        ? [nameof(PublishTarget.Both), nameof(PublishTarget.Facebook)]
        : [nameof(PublishTarget.Facebook)];

    /// <summary>What must succeed before THIS draft counts as Published — the per-draft
    /// replacement for PublishJob's old process-wide field. Facebook joins a Both draft's set
    /// only when it is configured, so a site-only deployment keeps reaching Published exactly as
    /// it does today.</summary>
    public static IReadOnlyList<string> RequiredDestinations(
        PublishTarget target, bool facebookConfigured, bool facebookOnly)
    {
        if (facebookOnly)
            return [PublishDestinations.Facebook];

        return target switch
        {
            PublishTarget.Website => [PublishDestinations.Umbraco],
            PublishTarget.Facebook => [PublishDestinations.Facebook],
            _ => facebookConfigured
                ? [PublishDestinations.Umbraco, PublishDestinations.Facebook]
                : [PublishDestinations.Umbraco],
        };
    }
}
