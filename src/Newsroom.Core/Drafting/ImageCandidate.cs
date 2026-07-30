namespace Newsroom.Core.Drafting;

/// <summary>nw_DraftImage.SourceKind values (ADR-0009 sourcing tiers).</summary>
public static class ImageSourceKinds
{
    /// <summary>Free stock photo (tier 2 — Pexels, Pixabay); Url is the provider URL the
    /// site fetches server-side.</summary>
    public const string Stock = "stock";

    /// <summary>AI-generated illustration (tier 3, ADR-0011); Url is a worker-local file path
    /// that publishers inline/upload, like an editor upload.</summary>
    public const string Ai = "ai";

    /// <summary>Editor photo upload via Telegram reply (tier 4); Url is a worker-local file path.</summary>
    public const string EditorUpload = "editor-upload";
}

/// <summary>One cover-image suggestion for a draft (docs/05-integrations/images.md, ADR-0009),
/// carrying the attribution the licence requires. For <see cref="ImageSourceKinds.Ai"/>
/// candidates, Url is the worker-local path of the saved generation, not a provider URL.</summary>
public sealed record ImageCandidate(
    string Url,
    string? ThumbUrl,
    string ProviderName,
    string? Attribution,
    int Width,
    int Height,
    string SourceKind = ImageSourceKinds.Stock);
