using Microsoft.Extensions.Logging;

using Newsroom.Core.Images;

using SkiaSharp;

namespace Newsroom.Infrastructure.Images;

/// <summary>
/// The two local pixel operations the cover pipeline needs (ADR-0013), both deliberately kept out
/// of the image model:
///
/// 1. <see cref="OverlayLogo"/> — composites the real Predel News logo onto a generated cover.
///    Diffusion models reproduce a wordmark approximately; a brand asset must be exact, so the
///    prompt forbids generating any logo and the genuine PNG is drawn on afterwards.
/// 2. <see cref="ShrinkToReferenceLimit"/> — downscales a public-figure reference photo so both
///    sides sit under Cloudflare's 512 px limit for <c>input_image_N</c>, preserving aspect ratio
///    and never upscaling. A portrait that is merely too large is a resize, not a reason to drop
///    the likeness.
///
/// Both are best-effort by contract: on any decode/encode failure they return the input unchanged
/// (or false) and log, because a cosmetic overlay must never cost the whole cover.
/// </summary>
public sealed class ImageCompositor(ILogger<ImageCompositor> logger)
{
    /// <summary>JPEG quality for the re-encoded cover — high enough that the recompression after
    /// the overlay is invisible, low enough to stay well inside Facebook/OG size budgets.</summary>
    public const int JpegQuality = 92;

    /// <summary>
    /// Draws <paramref name="logoBytes"/> into <paramref name="corner"/> of
    /// <paramref name="coverBytes"/> and returns the re-encoded JPEG. Returns the original bytes
    /// unchanged when either image cannot be decoded.
    /// </summary>
    /// <param name="widthPercent">Logo width as a percentage of the cover width.</param>
    /// <param name="marginPercent">Margin from both edges, as a percentage of the cover width.</param>
    public byte[] OverlayLogo(
        byte[] coverBytes, byte[] logoBytes, CoverLogoCorner corner,
        double widthPercent, double marginPercent)
    {
        try
        {
            using var cover = SKBitmap.Decode(coverBytes);
            using var logo = SKBitmap.Decode(logoBytes);
            if (cover is null || logo is null || cover.Width == 0 || logo.Width == 0)
            {
                logger.LogWarning("Logo overlay skipped: cover or logo bytes could not be decoded");
                return coverBytes;
            }

            var logoWidth = Math.Max(1, (int)Math.Round(cover.Width * widthPercent / 100.0));
            var logoHeight = Math.Max(1, (int)Math.Round(logoWidth * (double)logo.Height / logo.Width));
            var margin = (int)Math.Round(cover.Width * marginPercent / 100.0);

            var x = corner is CoverLogoCorner.UpperLeft or CoverLogoCorner.LowerLeft
                ? margin
                : cover.Width - logoWidth - margin;
            var y = corner is CoverLogoCorner.UpperLeft or CoverLogoCorner.UpperRight
                ? margin
                : cover.Height - logoHeight - margin;

            using var surface = SKSurface.Create(new SKImageInfo(cover.Width, cover.Height));
            using var coverImage = SKImage.FromBitmap(cover);
            using var logoImage = SKImage.FromBitmap(logo);
            var canvas = surface.Canvas;
            canvas.DrawImage(coverImage, new SKRect(0, 0, cover.Width, cover.Height), Sampling);
            canvas.DrawImage(logoImage, new SKRect(x, y, x + logoWidth, y + logoHeight), Sampling);
            canvas.Flush();

            return Encode(surface) ?? coverBytes;
        }
        catch (Exception ex)
        {
            // Never fail a cover over its logo — the editor still sees the image in review.
            logger.LogWarning(ex, "Logo overlay failed; keeping the generated cover as-is");
            return coverBytes;
        }
    }

    /// <summary>
    /// Downscales <paramref name="bytes"/> so both sides are below
    /// <see cref="ImageDimensions.MaxReferenceSide"/>, preserving aspect ratio. Returns false when
    /// the image cannot be decoded; returns true with the bytes unchanged when it is already small
    /// enough (never upscales).
    /// </summary>
    public bool ShrinkToReferenceLimit(byte[] bytes, out byte[] result)
    {
        result = bytes;
        try
        {
            if (ImageDimensions.IsUsableReference(bytes))
                return true; // already under the limit — do not re-encode, do not upscale

            using var source = SKBitmap.Decode(bytes);
            if (source is null || source.Width == 0 || source.Height == 0)
                return false;

            // 511 is the largest side Cloudflare accepts ("smaller than 512x512").
            const int max = ImageDimensions.MaxReferenceSide - 1;
            var scale = Math.Min((double)max / source.Width, (double)max / source.Height);
            if (scale >= 1)
                return true; // decodable, already inside the limit by pixel size

            var width = Math.Max(1, (int)Math.Floor(source.Width * scale));
            var height = Math.Max(1, (int)Math.Floor(source.Height * scale));

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            using var sourceImage = SKImage.FromBitmap(source);
            surface.Canvas.DrawImage(sourceImage, new SKRect(0, 0, width, height), Sampling);
            surface.Canvas.Flush();

            var encoded = Encode(surface);
            if (encoded is null)
                return false;

            result = encoded;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reference photo could not be resized; the cover stays anonymous");
            return false;
        }
    }

    /// <summary>Bilinear + mipmaps: the quality worth having when downscaling a portrait or
    /// fitting a logo, without the cost of the highest cubic modes.</summary>
    private static SKSamplingOptions Sampling { get; } =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private static byte[]? Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data?.ToArray();
    }
}
