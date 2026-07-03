namespace Newsroom.Core.Drafting;

/// <summary>
/// Lifecycle of a draft (nw_Draft.Status) — the state machine in docs/02-functional-spec.md.
/// Phase 3 produces PendingReview (or GenerationFailed) rows; the Telegram review surface
/// (Phase 4) and publishing (Phase 5) drive the rest of the transitions.
/// </summary>
public enum DraftStatus
{
    Generating,
    PendingReview,
    Approved,
    Rejected,
    Expired,
    Publishing,
    Published,
    PartiallyPublished,
    PublishFailed,
    GenerationFailed
}
