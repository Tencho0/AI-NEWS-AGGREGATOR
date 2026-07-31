using System.Buffers.Binary;

namespace Newsroom.Core.Images;

/// <summary>
/// Header-only pixel-size reader for PNG and JPEG. Exists for one rule: Cloudflare rejects
/// FLUX.2 reference images that are 512×512 or larger, and a rejected request costs the whole
/// cover (the draft falls back to stock). Checking the header is cheaper than a decoding
/// dependency and enough to skip an oversized reference before it is ever sent.
/// </summary>
public static class ImageDimensions
{
    /// <summary>Cloudflare's limit: every FLUX.2 <c>input_image_N</c> must be smaller than this
    /// in both dimensions.</summary>
    public const int MaxReferenceSide = 512;

    /// <summary>True when the bytes are a PNG/JPEG whose two sides are both under
    /// <see cref="MaxReferenceSide"/>. Unrecognised formats return false — the reference is
    /// skipped rather than gambled with.</summary>
    public static bool IsUsableReference(ReadOnlySpan<byte> bytes) =>
        TryRead(bytes, out var width, out var height)
        && width is > 0 and < MaxReferenceSide
        && height is > 0 and < MaxReferenceSide;

    /// <summary>Reads the pixel size from a PNG or JPEG header. False for anything else.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        return TryReadPng(bytes, ref width, ref height) || TryReadJpeg(bytes, ref width, ref height);
    }

    /// <summary>PNG: 8-byte signature, then the IHDR chunk whose data starts at byte 16 with
    /// big-endian width and height.</summary>
    private static bool TryReadPng(ReadOnlySpan<byte> bytes, ref int width, ref int height)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature))
            return false;

        width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        return true;
    }

    /// <summary>JPEG: walk the marker segments from SOI to the first start-of-frame (SOF0..SOF15,
    /// skipping the non-frame markers in that range), whose payload carries height then width.</summary>
    private static bool TryReadJpeg(ReadOnlySpan<byte> bytes, ref int width, ref int height)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return false;

        var at = 2;
        while (at + 3 < bytes.Length)
        {
            if (bytes[at] != 0xFF)
            {
                at++; // padding/fill byte between segments
                continue;
            }

            var marker = bytes[at + 1];
            at += 2;
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) // standalone markers, no length
                continue;
            if (marker == 0xD9 || marker == 0xDA) // end of image / start of scan — no frame header found
                return false;
            if (at + 1 >= bytes.Length)
                return false;

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at, 2));
            if (length < 2 || at + length > bytes.Length)
                return false;

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                if (length < 7)
                    return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(at + 5, 2));
                return true;
            }

            at += length;
        }
        return false;
    }
}
