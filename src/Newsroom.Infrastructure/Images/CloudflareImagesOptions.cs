using Microsoft.Extensions.Configuration;

using Newsroom.Core.Images;

namespace Newsroom.Infrastructure.Images;

/// <summary>How the Cloudflare Workers AI run endpoint wants its parameters. The FLUX.2 family
/// (klein, dev) takes multipart/form-data even for a prompt-only request and is the only way to
/// send reference images; FLUX.1 Schnell takes JSON and no images.</summary>
public enum CloudflareRequestFormat
{
    Multipart = 0,
    Json = 1,
}

/// <summary>
/// Settings for the Cloudflare Workers AI cover-image generator (ADR-0011, ADR-0012, ADR-0013),
/// bound from configuration: <c>Images:Cloudflare:AccountId</c> / <c>Images:Cloudflare:ApiToken</c>
/// (either empty = generation disabled, stock providers only; real values live in
/// user-secrets / service environment variables, see docs/06-security.md),
/// <c>Images:Cloudflare:Model</c> + <c>RequestFormat</c>, <c>Steps</c> (FLUX.2 klein fixes steps at
/// 4 — leave it 0 there so the field is not sent at all), <c>Guidance</c> (0 = not sent), and
/// <c>Width</c>/<c>Height</c> (default 1280×720 — 16:9, above the site's 1200 px cover warning and
/// Google Discover's large-image minimum; FLUX.2 accepts 256..1920).
///
/// Where files live is <b>not</b> here — that is <see cref="ImageStorageOptions"/> /
/// <see cref="ImageStorage"/>, so the deployment directory stays disposable.
///
/// Cost safety (ADR-0012/0013): the default model is neuron-priced and sized to sit inside the
/// Workers AI free daily allocation. Nothing here ever escalates to a billable model — when the
/// allocation runs out the generator stops for the day and the editor is told.
/// </summary>
public sealed record CloudflareImagesOptions
{
    public const string DefaultModel = "@cf/black-forest-labs/flux-2-klein-4b";

    public string AccountId { get; init; } = "";
    public string ApiToken { get; init; } = "";
    public string Model { get; init; } = DefaultModel;
    public CloudflareRequestFormat RequestFormat { get; init; } = CloudflareRequestFormat.Multipart;

    /// <summary>Diffusion steps. 0 = omit (FLUX.2 klein is distilled and fixes steps at 4;
    /// sending the field is a validation error).</summary>
    public int Steps { get; init; }

    /// <summary>Prompt-adherence guidance. 0 = omit and take the model's default.</summary>
    public double Guidance { get; init; }

    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;

    /// <summary>Extra attempts after a transient capacity error (Cloudflare code 3040). Applies to
    /// nothing else — a spend wall is never retried, a rejected prompt is never retried.</summary>
    public int TransientRetries { get; init; } = 2;

    /// <summary>Base backoff between transient retries, multiplied by the attempt number. 0 in
    /// tests.</summary>
    public double TransientRetryDelaySeconds { get; init; } = 3;

    /// <summary>Storage key of the real Predel News logo PNG, composited onto every generated
    /// cover (ADR-0013 — the model is forbidden to draw one). Empty = no overlay.</summary>
    public string LogoFile { get; init; } = "branding/predel-news-logo.png";

    public CoverLogoCorner LogoCorner { get; init; } = CoverLogoCorner.UpperRight;

    /// <summary>Logo width as a percentage of the cover width.</summary>
    public double LogoWidthPercent { get; init; } = 16;

    /// <summary>Logo margin from both edges, as a percentage of the cover width.</summary>
    public double LogoMarginPercent { get; init; } = 3;

    /// <summary>
    /// Whether the image model is asked to burn the cover-text plan into the picture (ADR-0013).
    /// Off since 2026-07-31: FLUX.2 klein cannot spell Cyrillic — live output rendered
    /// "Обновени сгради" as "Обоввейк сргади" and "икономия" as "иономітё", using і and ё, which
    /// are not Bulgarian letters at all. ADR-0013 pre-committed this switch ("if misspellings turn
    /// out to be frequent … the prompt drops back to text-free") until the local text renderer
    /// lands. Off takes the composer's already-supported text-free path: no glyphs are requested,
    /// and the headline is passed as context the model must not render.
    /// </summary>
    public bool BurnInCoverText { get; init; } = true;

    /// <summary>Public figures whose likeness may appear on a cover (ADR-0012). Empty — the
    /// default — means covers never depict a real identifiable person.</summary>
    public IReadOnlyList<PublicFigure> PublicFigures { get; init; } = [];

    /// <summary>Off by default: crime and scandal covers stay symbolic rather than putting a
    /// recognisable face next to an allegation (<see cref="CoverPersonPolicy"/>).</summary>
    public bool AllowPublicFiguresInSensitiveCategories { get; init; }

    public static CloudflareImagesOptions From(IConfiguration configuration) => new()
    {
        AccountId = configuration.GetValue("Images:Cloudflare:AccountId", "")!,
        ApiToken = configuration.GetValue("Images:Cloudflare:ApiToken", "")!,
        Model = configuration.GetValue("Images:Cloudflare:Model", DefaultModel)!,
        RequestFormat = configuration.GetValue(
            "Images:Cloudflare:RequestFormat", CloudflareRequestFormat.Multipart),
        Steps = configuration.GetValue("Images:Cloudflare:Steps", 0),
        Guidance = configuration.GetValue("Images:Cloudflare:Guidance", 0d),
        Width = configuration.GetValue("Images:Cloudflare:Width", 1280),
        Height = configuration.GetValue("Images:Cloudflare:Height", 720),
        TransientRetries = configuration.GetValue("Images:Cloudflare:TransientRetries", 2),
        TransientRetryDelaySeconds = configuration.GetValue("Images:Cloudflare:TransientRetryDelaySeconds", 3d),
        LogoFile = configuration.GetValue("Images:Cover:LogoFile", "branding/predel-news-logo.png")!,
        LogoCorner = configuration.GetValue("Images:Cover:LogoCorner", CoverLogoCorner.UpperRight),
        LogoWidthPercent = configuration.GetValue("Images:Cover:LogoWidthPercent", 16d),
        LogoMarginPercent = configuration.GetValue("Images:Cover:LogoMarginPercent", 3d),
        BurnInCoverText = configuration.GetValue("Images:Cover:BurnInText", true),
        PublicFigures = ReadPublicFigures(configuration),
        AllowPublicFiguresInSensitiveCategories = configuration.GetValue(
            "Images:Cloudflare:AllowPublicFiguresInSensitiveCategories", false),
    };

    /// <summary>Reads <c>Images:PublicFigures</c>. Entries without a name or without a reference
    /// image file name are dropped — a figure with no approved photo can never be depicted, so
    /// carrying it would only invite a name-only likeness.</summary>
    private static IReadOnlyList<PublicFigure> ReadPublicFigures(IConfiguration configuration) =>
        configuration.GetSection("Images:PublicFigures").GetChildren()
            .Select(section => new PublicFigure(
                Name: section.GetValue("Name", "")!.Trim(),
                Role: section.GetValue("Role", "")!.Trim(),
                ReferenceImage: section.GetValue("ReferenceImage", "")!.Trim(),
                Aliases: section.GetSection("Aliases").Get<string[]>() ?? []))
            .Where(f => f.Name.Length > 0 && f.ReferenceImage.Length > 0)
            .ToList();
}

/// <summary>
/// Where image files live (ADR-0013). <c>Images:StorageRoot</c> is a persistent location **outside**
/// the worker's deployment directory — in production a mounted volume — so reinstalling or wiping
/// the install folder never destroys draft covers, editor uploads or the public-figure reference
/// photos. The three area names are relative folders beneath it.
/// </summary>
public sealed record ImageStorageOptions
{
    public string Root { get; init; } = DefaultRoot();
    public string GeneratedDir { get; init; } = "generated-images";
    public string EditorUploadDir { get; init; } = "editor-uploads";
    public string ReferenceDir { get; init; } = "public-figures";

    public static ImageStorageOptions From(IConfiguration configuration) => new()
    {
        Root = Blank(configuration.GetValue("Images:StorageRoot", "")) ? DefaultRoot()
            : configuration.GetValue("Images:StorageRoot", "")!,
        GeneratedDir = configuration.GetValue("Images:Cloudflare:GeneratedImageDir", "generated-images")!,
        EditorUploadDir = configuration.GetValue("Images:EditorUploadDir", "editor-uploads")!,
        ReferenceDir = configuration.GetValue("Images:Cloudflare:ReferenceImageDir", "public-figures")!,
    };

    /// <summary>The storage, with the old deployment directory registered as the legacy root so
    /// pre-ADR-0013 absolute paths in nw_DraftImage still resolve.</summary>
    public ImageStorage CreateStorage() =>
        new(Root, GeneratedDir, EditorUploadDir, ReferenceDir, AppContext.BaseDirectory);

    /// <summary>Per-machine shared data, never the install folder: <c>%ProgramData%</c> on Windows,
    /// <c>/usr/share</c> on Linux. Overridden by <c>Images:StorageRoot</c> in production.</summary>
    private static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PredelNewsroom", "images");

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
