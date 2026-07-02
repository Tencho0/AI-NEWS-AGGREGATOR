using System.Security.Cryptography;
using System.Text;

namespace Newsroom.Core.Scraping;

public static class HashUtil
{
    /// <summary>Lower-case SHA-256 hex of the UTF-8 bytes.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Content hash for wire-copy detection: whitespace runs collapsed, trimmed,
    /// case preserved. Same agency text with different formatting hashes identically.
    /// </summary>
    public static string ContentHash(string title, string? text)
    {
        var normalized = Normalize(title) + "\n" + Normalize(text ?? string.Empty);
        return Sha256Hex(normalized);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
