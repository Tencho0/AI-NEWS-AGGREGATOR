namespace Newsroom.Core.Publishing;

/// <summary>What the site's publishing endpoint returned (docs/05-integrations/umbraco.md):
/// the content's key, its live URL, and whether the externalRef had already been published
/// (an idempotent re-post after a crash or timeout).</summary>
public sealed record UmbracoPublishResult(Guid ContentKey, string Url, bool AlreadyExisted);

/// <summary>nw_PublishRecord.Destination values. Facebook joins in Phase 6.</summary>
public static class PublishDestinations
{
    public const string Umbraco = "umbraco";
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

    /// <summary>Inserts the Succeeded record and flips the draft to Published, in one
    /// transaction (v1 has a single destination; Phase 6 refines this to
    /// PartiallyPublished semantics).</summary>
    Task RecordSuccessAsync(
        long draftId, string destination, string externalId, string url, CancellationToken ct);

    /// <summary>Inserts a Failed record worth <paramref name="attempts"/> attempts — transient
    /// failures pass 1, terminal rejections pass the cap so they are never retried. When the
    /// destination's summed attempts reach <paramref name="maxAttempts"/>, the draft flips to
    /// PublishFailed in the same transaction.</summary>
    /// <returns>True when the draft has now exhausted its attempts for the destination —
    /// the caller alerts the editor exactly once.</returns>
    Task<bool> RecordFailureAsync(
        long draftId, string destination, string error, int attempts, int maxAttempts,
        CancellationToken ct);
}
