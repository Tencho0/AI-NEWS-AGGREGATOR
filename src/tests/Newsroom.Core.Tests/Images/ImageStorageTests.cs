using Newsroom.Core.Images;

namespace Newsroom.Core.Tests.Images;

public class ImageStorageTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "nw-store-root");
    private static readonly string Legacy = Path.Combine(Path.GetTempPath(), "nw-legacy-bin");

    private static ImageStorage Create(bool withLegacy = true) =>
        new(Root, legacyRoot: withLegacy ? Legacy : null);

    [Fact]
    public void Areas_resolve_beneath_the_storage_root()
    {
        var storage = Create();

        Assert.Equal(Path.Combine(storage.Root, "generated-images"), storage.GeneratedDirectory);
        Assert.Equal(Path.Combine(storage.Root, "editor-uploads"), storage.EditorUploadDirectory);
        Assert.Equal(Path.Combine(storage.Root, "public-figures"), storage.ReferenceDirectory);
    }

    [Fact]
    public void A_key_is_forward_slashed_and_area_scoped()
    {
        var storage = Create();

        Assert.Equal("generated-images/flux-1.jpg", storage.GeneratedKey("flux-1.jpg"));
        Assert.Equal("editor-uploads/photo.jpg", storage.EditorUploadKey("photo.jpg"));
        // A caller passing a whole path only contributes its file name.
        Assert.Equal("generated-images/flux-1.jpg", storage.GeneratedKey(@"C:\somewhere\flux-1.jpg"));
    }

    [Fact]
    public void A_relative_key_resolves_under_the_root()
    {
        var storage = Create();

        Assert.True(storage.TryResolve("generated-images/flux-1.jpg", out var path));
        Assert.Equal(Path.Combine(storage.GeneratedDirectory, "flux-1.jpg"), path);
    }

    [Fact]
    public void KeyFor_round_trips_an_absolute_path_inside_the_root()
    {
        var storage = Create();
        var absolute = Path.Combine(storage.GeneratedDirectory, "flux-1.jpg");

        var key = storage.KeyFor(absolute);

        Assert.Equal("generated-images/flux-1.jpg", key);
        Assert.True(storage.TryResolve(key, out var resolved));
        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void KeyFor_refuses_a_path_outside_the_root()
    {
        var storage = Create();

        Assert.Throws<InvalidOperationException>(
            () => storage.KeyFor(Path.Combine(Path.GetTempPath(), "elsewhere", "x.jpg")));
    }

    [Theory]
    [InlineData("../../../windows/system32/config/sam")]
    [InlineData("generated-images/../../escape.jpg")]
    [InlineData("generated-images/../../../etc/passwd")]
    public void Traversal_keys_are_refused(string key)
    {
        var storage = Create();

        Assert.False(storage.TryResolve(key, out _));
        Assert.Throws<InvalidOperationException>(() => storage.Resolve(key));
    }

    [Fact]
    public void A_traversal_key_that_lands_back_inside_the_root_is_still_resolvable()
    {
        // Not an escape: it normalizes to a path under the root, so it is safe by definition.
        var storage = Create();

        Assert.True(storage.TryResolve("generated-images/../editor-uploads/photo.jpg", out var path));
        Assert.Equal(Path.Combine(storage.EditorUploadDirectory, "photo.jpg"), path);
    }

    [Fact]
    public void Remote_urls_and_blanks_never_resolve_to_a_file()
    {
        var storage = Create();

        Assert.False(storage.TryResolve("https://images.pexels.com/photo/1.jpg", out _));
        Assert.False(storage.TryResolve("http://example.com/a.jpg", out _));
        Assert.False(storage.TryResolve("", out _));
        Assert.False(storage.TryResolve("   ", out _));
        Assert.False(storage.TryResolve(null, out _));
    }

    [Fact]
    public void A_legacy_absolute_path_inside_the_old_deployment_directory_still_resolves()
    {
        // Pre-ADR-0013 nw_DraftImage rows hold absolute paths under the worker's base directory.
        var storage = Create();
        var legacyFile = Path.Combine(Legacy, "generated-images", "flux-old.jpg");

        Assert.True(storage.TryResolve(legacyFile, out var path));
        Assert.Equal(Path.GetFullPath(legacyFile), path);
    }

    [Fact]
    public void An_absolute_path_in_neither_root_is_refused()
    {
        var storage = Create();

        Assert.False(storage.TryResolve(Path.Combine(Path.GetTempPath(), "stranger", "x.jpg"), out _));
        // And with no legacy root configured, even the old deployment path is refused.
        Assert.False(Create(withLegacy: false)
            .TryResolve(Path.Combine(Legacy, "generated-images", "flux-old.jpg"), out _));
    }

    [Fact]
    public void Only_the_generated_and_upload_areas_are_prunable()
    {
        var storage = Create();

        Assert.True(storage.IsPrunable(Path.Combine(storage.GeneratedDirectory, "flux-1.jpg")));
        Assert.True(storage.IsPrunable(Path.Combine(storage.EditorUploadDirectory, "photo.jpg")));
        // Reference portraits and the logo asset are reusable inputs — never auto-deleted.
        Assert.False(storage.IsPrunable(Path.Combine(storage.ReferenceDirectory, "ivanov.png")));
        Assert.False(storage.IsPrunable(Path.Combine(storage.Root, "branding", "logo.png")));
        Assert.False(storage.IsPrunable(Path.Combine(Path.GetTempPath(), "stranger", "x.jpg")));
        Assert.False(storage.IsPrunable(storage.Root));
    }

    [Fact]
    public void An_area_that_tries_to_escape_the_root_is_a_configuration_error()
    {
        Assert.Throws<ArgumentException>(() => new ImageStorage(Root, generatedArea: "../outside"));
        Assert.Throws<ArgumentException>(() => new ImageStorage(Root, editorUploadArea: @"C:\elsewhere"));
        Assert.Throws<ArgumentException>(() => new ImageStorage(Root, referenceArea: "  "));
        Assert.Throws<ArgumentException>(() => new ImageStorage(""));
    }

    [Fact]
    public void IsRemote_distinguishes_stock_urls_from_local_keys()
    {
        Assert.True(ImageStorage.IsRemote("https://cdn.pixabay.com/photo/1.jpg"));
        Assert.True(ImageStorage.IsRemote("HTTP://example.com/a.jpg"));
        Assert.False(ImageStorage.IsRemote("generated-images/flux-1.jpg"));
        Assert.False(ImageStorage.IsRemote(null));
    }
}
