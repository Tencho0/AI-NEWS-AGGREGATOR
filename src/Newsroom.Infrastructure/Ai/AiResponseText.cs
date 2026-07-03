namespace Newsroom.Infrastructure.Ai;

/// <summary>Shared cleanup for model responses: models sometimes wrap JSON in a markdown fence
/// despite instructions, and error messages should carry a payload preview, not the payload.</summary>
internal static class AiResponseText
{
    internal static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
            return trimmed;

        var body = trimmed[(firstLineEnd + 1)..];
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closingFence >= 0 ? body[..closingFence] : body).Trim();
    }

    internal static string Preview(string text) =>
        text.Length <= 200 ? text : text[..200] + "…";
}
