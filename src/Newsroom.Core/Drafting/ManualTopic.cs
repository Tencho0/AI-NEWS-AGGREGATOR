namespace Newsroom.Core.Drafting;

/// <summary>
/// Helpers for editor-authored articles (/post, /new — docs/05-integrations/telegram.md):
/// synthetic nw_Topic rows with Status=Manual whose EditorInput column carries the editor's
/// text. Pure — no I/O.
/// </summary>
public static class ManualTopic
{
    /// <summary>Source name shown for the synthetic bundle article built from EditorInput.</summary>
    public const string SourceName = "Редакция";

    private const int MaxLabelChars = 60;

    /// <summary>Topic label for an editor-authored article: the first non-empty line,
    /// truncated to 60 chars on a word boundary with an ellipsis.</summary>
    public static string LabelFrom(string text)
    {
        var firstLine = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        if (firstLine.Length <= MaxLabelChars)
            return firstLine;

        var cut = firstLine[..MaxLabelChars];
        var lastBreak = cut.LastIndexOf(' ');
        if (lastBreak > 0)
            cut = cut[..lastBreak];
        return cut.TrimEnd() + "…";
    }
}
