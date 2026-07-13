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

    /// <summary>nw_Draft.PromptVersion value marking a row as editor-authored (verbatim) —
    /// written by <c>DraftRepository.CreateManualArticleAsync</c> for /post drafts. A ✏️
    /// regeneration of such a draft is AI output and gets its own PromptVersion, so this
    /// constant identifies the draft row, not the topic.</summary>
    public const string EditorPromptVersion = "editor-v1";

    /// <summary>nw_Draft.Model value for editor-authored (verbatim) draft rows — shown as
    /// "модел editor" on the review card.</summary>
    public const string EditorModelName = "editor";

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
        else if (lastBreak < 0)
            cut = cut[..59];
        return cut.TrimEnd() + "…";
    }
}
