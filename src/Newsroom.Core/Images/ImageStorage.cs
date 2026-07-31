namespace Newsroom.Core.Images;

/// <summary>
/// The one place that turns a stored image reference into a file on disk (ADR-0013).
///
/// Images live under a persistent <c>Images:StorageRoot</c> that is deliberately **outside** the
/// worker's deployment directory, so the install folder stays disposable — a redeploy, a service
/// reinstall or a `bin` wipe must never take the drafts' covers with it. Inside the root there are
/// three areas: generated covers, editor uploads, and the reusable public-figure reference photos.
///
/// <c>nw_DraftImage.Url</c> stores a **relative key** (<c>generated-images/flux-….jpg</c>), never an
/// absolute path, so the root can move without rewriting rows. Rows written before ADR-0013 hold
/// absolute paths; those still resolve, but only when they land inside the storage root or the
/// legacy deployment directory. Everything else — including any key containing <c>..</c> — is
/// refused, which is the path-traversal guard: a hostile or corrupted Url can never reach a file
/// outside the areas this class owns.
/// </summary>
public sealed class ImageStorage
{
    private readonly string? legacyRoot;

    public ImageStorage(
        string root,
        string generatedArea = "generated-images",
        string editorUploadArea = "editor-uploads",
        string referenceArea = "public-figures",
        string? legacyRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        GeneratedArea = CleanArea(generatedArea, nameof(generatedArea));
        EditorUploadArea = CleanArea(editorUploadArea, nameof(editorUploadArea));
        ReferenceArea = CleanArea(referenceArea, nameof(referenceArea));
        this.legacyRoot = string.IsNullOrWhiteSpace(legacyRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
    }

    /// <summary>Absolute, normalized storage root. Every key resolves beneath it.</summary>
    public string Root { get; }

    public string GeneratedArea { get; }
    public string EditorUploadArea { get; }
    public string ReferenceArea { get; }

    public string GeneratedDirectory => Path.Combine(Root, GeneratedArea);
    public string EditorUploadDirectory => Path.Combine(Root, EditorUploadArea);
    public string ReferenceDirectory => Path.Combine(Root, ReferenceArea);

    /// <summary>The relative key for a file name inside an area — always forward-slashed, so the
    /// value in the database is platform-neutral.</summary>
    public string Key(string area, string fileName) => $"{area}/{Path.GetFileName(fileName)}";

    public string GeneratedKey(string fileName) => Key(GeneratedArea, fileName);

    public string EditorUploadKey(string fileName) => Key(EditorUploadArea, fileName);

    /// <summary>Creates the area directory if needed and returns its absolute path.</summary>
    public string EnsureDirectory(string area)
    {
        var dir = Path.Combine(Root, CleanArea(area, nameof(area)));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Resolves a stored reference to an absolute path. Returns false — never throws, never
    /// escapes — for remote URLs, blank values, traversal attempts, and legacy absolute paths
    /// pointing somewhere we do not own.
    /// </summary>
    public bool TryResolve(string? keyOrLegacyPath, out string absolutePath)
    {
        absolutePath = "";
        if (string.IsNullOrWhiteSpace(keyOrLegacyPath))
            return false;

        var value = keyOrLegacyPath.Trim();
        if (IsRemote(value))
            return false;

        string candidate;
        try
        {
            candidate = Path.IsPathRooted(value)
                // A pre-ADR-0013 row. Allowed only if it is still inside a directory we own.
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(Root, value.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // malformed value — treated exactly like a traversal attempt
        }

        if (!IsInside(candidate, Root) && !(legacyRoot is not null && IsInside(candidate, legacyRoot)))
            return false;

        absolutePath = candidate;
        return true;
    }

    /// <summary>Resolve-or-throw, for callers that already know the reference is one of ours.</summary>
    public string Resolve(string keyOrLegacyPath) =>
        TryResolve(keyOrLegacyPath, out var path)
            ? path
            : throw new InvalidOperationException(
                $"Image reference '{keyOrLegacyPath}' does not resolve inside the image storage root.");

    /// <summary>
    /// The relative key for an absolute path inside the root — how a freshly written file becomes
    /// a database value. Throws for a path outside the root, because storing such a key would
    /// create a row that can never be resolved again.
    /// </summary>
    public string KeyFor(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (!IsInside(full, Root))
            throw new InvalidOperationException(
                $"'{absolutePath}' is outside the image storage root '{Root}'.");

        return Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// True only for files in the two churn areas — generated covers and editor uploads. The
    /// retention pass deletes nothing else: the public-figure reference photos and the logo asset
    /// are reusable inputs, and anything outside the root is not ours to touch.
    /// </summary>
    public bool IsPrunable(string absolutePath) =>
        IsInside(absolutePath, GeneratedDirectory) || IsInside(absolutePath, EditorUploadDirectory);

    /// <summary>True when the reference is a local key/path rather than a remote stock URL.</summary>
    public static bool IsRemote(string? value) =>
        value is not null
        && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Containment test on normalized paths — the actual traversal guard. A path equal to
    /// the directory itself is not "inside" it, so an empty key cannot resolve to the root.</summary>
    public static bool IsInside(string absolutePath, string directory)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var path = Path.GetFullPath(absolutePath);
        return path.Length > dir.Length
            && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
            && (path[dir.Length] == Path.DirectorySeparatorChar
                || path[dir.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>An area is a single relative folder name — rooted or traversing values are a
    /// configuration error, caught at startup rather than at the first save.</summary>
    private static string CleanArea(string area, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area, paramName);
        var trimmed = area.Trim().Replace('\\', '/').Trim('/');
        if (trimmed.Length == 0 || trimmed.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(trimmed))
            throw new ArgumentException(
                $"Image storage area '{area}' must be a relative folder name inside the storage root.",
                paramName);
        return trimmed;
    }
}
