using Microsoft.Extensions.AI;

namespace Newsroom.Infrastructure.Ai;

/// <summary>Shared cleanup for model responses: models sometimes wrap JSON in a markdown fence
/// despite instructions, and error messages should carry a payload preview, not the payload.</summary>
internal static class AiResponseText
{
    /// <summary>Returns the response text, or throws <see cref="AiEmptyResponseException"/> if the
    /// model returned a 200 with an empty completion (provider load; classified transient by
    /// <see cref="AiTransientErrors"/>) — parsing "" would misreport it as malformed JSON.</summary>
    internal static string RequireCompletion(ChatResponse response, string what)
    {
        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new AiEmptyResponseException(what, response.FinishReason?.ToString());
        return text;
    }

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
