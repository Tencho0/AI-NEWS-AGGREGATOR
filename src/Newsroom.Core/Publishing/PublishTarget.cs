namespace Newsroom.Core.Publishing;

/// <summary>
/// Where an approved draft goes, chosen per draft by the editor at ✅ time
/// (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md). Persisted as its name
/// in nw_Draft.PublishTarget, the same way DraftStatus is stored.
/// </summary>
public enum PublishTarget
{
    /// <summary>The website, then the live link posted to the Facebook page — the flow that
    /// existed before targets, and still what ✅ Одобри and 📅 Насрочи mean.</summary>
    Both,

    /// <summary>The website only; nothing reaches the Facebook page.</summary>
    Website,

    /// <summary>The Facebook page only, as a standalone post (caption + image, no link) — the
    /// article never reaches the site.</summary>
    Facebook,
}
