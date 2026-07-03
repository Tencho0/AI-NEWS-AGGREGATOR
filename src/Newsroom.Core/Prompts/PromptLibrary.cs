using System.Reflection;

namespace Newsroom.Core.Prompts;

/// <summary>
/// Versioned prompt artifacts (docs/05-integrations/ai-generation.md). The editorial style guide
/// is single-sourced from <c>docs/editorial-style-guide.md</c>, embedded at build time: only the
/// Bulgarian block between the PROMPT-START/PROMPT-END markers goes into the drafting prompt;
/// English notes around it are documentation, not prompt.
/// </summary>
public static class PromptLibrary
{
    private const string StyleGuideResourceSuffix = "editorial-style-guide.md";
    private const string PromptStartMarker = "<!-- PROMPT-START -->";
    private const string PromptEndMarker = "<!-- PROMPT-END -->";

    private static readonly Lazy<string> LazyStyleGuide = new(LoadStyleGuide);

    /// <summary>The style-guide text pasted verbatim into the drafting system prompt.</summary>
    public static string EditorialStyleGuide => LazyStyleGuide.Value;

    private static string LoadStyleGuide()
    {
        var assembly = typeof(PromptLibrary).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(StyleGuideResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded resource '*{StyleGuideResourceSuffix}' not found in {assembly.GetName().Name}. " +
                "Is docs/editorial-style-guide.md still referenced by Newsroom.Core.csproj?");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return ExtractPromptBlock(reader.ReadToEnd());
    }

    /// <summary>Extracts the text between the PROMPT-START and PROMPT-END markers.</summary>
    internal static string ExtractPromptBlock(string document)
    {
        var start = document.IndexOf(PromptStartMarker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException(
                $"Editorial style guide is missing the '{PromptStartMarker}' marker — " +
                "the drafting prompt cannot tell style rules from documentation.");

        var blockStart = start + PromptStartMarker.Length;
        var end = document.IndexOf(PromptEndMarker, blockStart, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException(
                $"Editorial style guide is missing the '{PromptEndMarker}' marker — " +
                "the drafting prompt cannot tell style rules from documentation.");

        var block = document[blockStart..end].Trim();
        if (block.Length == 0)
            throw new InvalidOperationException(
                "Editorial style guide prompt block between the markers is empty.");
        return block;
    }
}
