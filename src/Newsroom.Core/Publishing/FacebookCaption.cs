namespace Newsroom.Core.Publishing;

/// <summary>
/// Composes the final Facebook message for a caption-carrying draft
/// (docs/superpowers/specs/2026-07-17-facebook-engagement-design.md): the AI-written social
/// caption followed by a blank line and the hashtags. Posted verbatim — deliberately NOT run
/// through <see cref="FacebookTeaser.StripMarkdown"/>, which deletes '#' (the validator already
/// guarantees the caption itself carries no markdown). Pure — no I/O, no configuration.
/// </summary>
public static class FacebookCaption
{
    public static string Compose(string caption, IReadOnlyList<string> hashtags)
    {
        var text = caption.Trim();
        var tags = string.Join(' ', hashtags.Where(h => !string.IsNullOrWhiteSpace(h)));
        return tags.Length == 0 ? text : $"{text}\n\n{tags}";
    }
}
