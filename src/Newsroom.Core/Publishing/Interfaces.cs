namespace Newsroom.Core.Publishing;

/// <summary>What the site's publishing endpoint returned (docs/05-integrations/umbraco.md):
/// the content's key, its live URL, and whether the externalRef had already been published
/// (an idempotent re-post after a crash or timeout).</summary>
public sealed record UmbracoPublishResult(Guid ContentKey, string Url, bool AlreadyExisted);

/// <summary>What the Graph API returned for a page post: the post id and, when the
/// follow-up permalink fetch succeeded (best-effort), the post's public URL.</summary>
public sealed record FacebookPostResult(string PostId, string? PermalinkUrl);

/// <summary>nw_PublishRecord.Destination values.</summary>
public static class PublishDestinations
{
    public const string Umbraco = "umbraco";
    public const string Facebook = "facebook";
}

/// <summary>
/// Thin seam over the Umbraco site's publishing endpoint (docs/05-integrations/umbraco.md,
/// ADR-0007). The endpoint owns all site invariants (media creation, slugs, picker
/// resolution, publish); this side only ships the approved content.
/// </summary>
public interface IUmbracoPublisher
{
    /// <summary>Publishes one article. Throws <see cref="PublishRejectedException"/> when the
    /// endpoint refuses the payload (400 — permanent, never retried); transient failures
    /// (network, 5xx) surface as ordinary exceptions and are retried by the caller.</summary>
    Task<UmbracoPublishResult> PublishAsync(ArticleToPublish article, CancellationToken ct);
}

/// <summary>
/// Thin seam over the Facebook Graph API (docs/05-integrations/facebook.md, ADR-0008): a link
/// post to the Predel News page after the site publish succeeded. Runs in dry-run mode by
/// default (Facebook:DryRun) so a configured token never posts by surprise.
/// </summary>
public interface IFacebookPublisher
{
    /// <summary>Posts one link post. Throws <see cref="PublishRejectedException"/> when the
    /// Graph API refuses the post (described 4xx — permanent, never retried; an invalid/expired
    /// token is called out in the reason); transient failures (network, 5xx) surface as
    /// ordinary exceptions and are retried by the caller.</summary>
    Task<FacebookPostResult> PublishAsync(FacebookPost post, CancellationToken ct);

    /// <summary>True when the page access token is still healthy (tokens expire and get
    /// invalidated server-side — docs/05-integrations/facebook.md). Never throws: an unreachable
    /// Graph API reads as unhealthy.</summary>
    Task<bool> CheckTokenAsync(CancellationToken ct);
}

/// <summary>
/// Persistence for publishing (docs/02-functional-spec.md §6). nw_PublishRecord is
/// per-destination, so Phase 6 adds a destination value, not a schema change. Attempt gating
/// is data-driven: a draft stops being selected once a Succeeded row exists for the
/// destination or its summed failed attempts reach the cap.
/// </summary>
public interface IPublishRepository
{
    /// <summary>Approved drafts with no Succeeded record for <paramref name="destination"/>
    /// and summed failed attempts below <paramref name="maxAttempts"/>, shaped for the
    /// endpoint: TagsJson deserialized, and the draft's chosen image (Selected, else lowest
    /// Ordinal) attached — null when there is none or the pick is an editor upload
    /// (Telegram file_ids are not resolvable by the site in v1).</summary>
    Task<IReadOnlyList<ArticleToPublish>> GetApprovedUnpublishedAsync(
        string destination, int maxAttempts, int maxCount, CancellationToken ct);

    /// <summary>Drafts whose site publish Succeeded but whose Facebook post is still pending:
    /// status PartiallyPublished, no Succeeded 'facebook' record, and summed failed 'facebook'
    /// attempts below <paramref name="maxAttempts"/> — shaped for the Graph API with the teaser
    /// already composed (<see cref="FacebookTeaser"/>) and the live URL taken from the
    /// draft's Succeeded 'umbraco' record.</summary>
    Task<IReadOnlyList<FacebookPost>> GetPendingFacebookAsync(
        int maxAttempts, int maxCount, CancellationToken ct);

    /// <summary>Inserts the Succeeded record and recalculates the draft status in the same
    /// transaction: Published when every destination in <paramref name="requiredDestinations"/>
    /// has a Succeeded record, PartiallyPublished otherwise — the site is live while Facebook
    /// is still pending (docs/02-functional-spec.md §6).</summary>
    Task RecordSuccessAsync(
        long draftId, string destination, string externalId, string? url,
        IReadOnlyCollection<string> requiredDestinations, CancellationToken ct);

    /// <summary>Inserts a Failed record worth <paramref name="attempts"/> attempts — transient
    /// failures pass 1, terminal rejections pass the cap so they are never retried. When the
    /// destination's summed attempts reach <paramref name="maxAttempts"/>, an Approved draft
    /// flips to PublishFailed in the same transaction; a PartiallyPublished draft keeps its
    /// status (the site is already live — only the Facebook leg is exhausted).</summary>
    /// <returns>True when the draft has now exhausted its attempts for the destination —
    /// the caller alerts the editor exactly once.</returns>
    Task<bool> RecordFailureAsync(
        long draftId, string destination, string error, int attempts, int maxAttempts,
        CancellationToken ct);
}
