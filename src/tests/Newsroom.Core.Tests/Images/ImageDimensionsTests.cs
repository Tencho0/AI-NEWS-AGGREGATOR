using System.Buffers.Binary;

using Newsroom.Core.Images;

namespace Newsroom.Core.Tests.Images;

public class ImageDimensionsTests
{
    [Theory]
    [InlineData(320, 400)]
    [InlineData(1, 1)]
    [InlineData(1024, 768)]
    public void Reads_png_dimensions(int width, int height)
    {
        Assert.True(ImageDimensions.TryRead(Png(width, height), out var w, out var h));
        Assert.Equal(width, w);
        Assert.Equal(height, h);
    }

    [Theory]
    [InlineData(320, 400)]
    [InlineData(4000, 3000)]
    public void Reads_jpeg_dimensions_past_the_leading_segments(int width, int height)
    {
        Assert.True(ImageDimensions.TryRead(Jpeg(width, height), out var w, out var h));
        Assert.Equal(width, w);
        Assert.Equal(height, h);
    }

    [Fact]
    public void Unrecognised_bytes_are_not_readable()
    {
        Assert.False(ImageDimensions.TryRead([1, 2, 3, 4, 5, 6, 7, 8], out _, out _));
        Assert.False(ImageDimensions.TryRead([], out _, out _));
    }

    [Fact]
    public void A_reference_under_the_cloudflare_limit_is_usable()
    {
        Assert.True(ImageDimensions.IsUsableReference(Png(511, 511)));
        Assert.True(ImageDimensions.IsUsableReference(Jpeg(320, 400)));
    }

    [Fact]
    public void A_reference_at_or_over_512px_on_either_side_is_rejected()
    {
        // Cloudflare requires input images strictly smaller than 512×512.
        Assert.False(ImageDimensions.IsUsableReference(Png(512, 400)));
        Assert.False(ImageDimensions.IsUsableReference(Png(400, 512)));
        Assert.False(ImageDimensions.IsUsableReference(Jpeg(1024, 768)));
    }

    [Fact]
    public void An_unreadable_reference_is_rejected_rather_than_gambled_with()
    {
        Assert.False(ImageDimensions.IsUsableReference([0x47, 0x49, 0x46, 0x38])); // GIF
        Assert.False(ImageDimensions.IsUsableReference([]));
    }

    /// <summary>Minimal PNG: signature + IHDR length/type/width/height.</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13); // IHDR length
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    /// <summary>Minimal JPEG: SOI, an APP0 segment to skip over, then SOF0 with the size.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        bytes.AddRange([0xFF, 0xE0, 0x00, 0x06, 1, 2, 3, 4]); // APP0, length 6 (4 payload bytes)

        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]); // SOF0, length 17, 8-bit precision
        bytes.AddRange([(byte)(height >> 8), (byte)(height & 0xFF)]);
        bytes.AddRange([(byte)(width >> 8), (byte)(width & 0xFF)]);
        bytes.AddRange(new byte[10]); // component spec padding
        return [.. bytes];
    }
}
