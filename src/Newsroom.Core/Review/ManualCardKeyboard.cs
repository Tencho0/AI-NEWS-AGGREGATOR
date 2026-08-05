namespace Newsroom.Core.Review;

/// <summary>
/// Which inline keyboard a manual-topic (/post) review card shows, driven by whether Category is
/// set (docs/superpowers/specs/2026-08-05-post-command-metadata-picker-design.md). Non-manual
/// (AI-drafted, trend-scored) cards never use this — they keep the fixed ✅/✏️/❌(/📅) keyboard.
/// </summary>
public enum ManualCardKeyboard
{
    /// <summary>Category is empty: category-picker buttons + ✏️/❌ only — no ✅/📅, so a draft
    /// with no publishable category cannot be approved.</summary>
    AwaitingCategory,

    /// <summary>Category is set: the normal ✅/✏️/❌(/📅) row plus a single 🏷 correction button.</summary>
    Resolved,

    /// <summary>🏷 pressed: Resolved's buttons stay, category + region picker rows are appended
    /// below so an already-valid category can still be corrected without AI.</summary>
    Expanded,
}
