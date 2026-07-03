using Newsroom.Core.Prompts;

namespace Newsroom.Core.Tests.Prompts;

public class PromptLibraryTests
{
    [Fact]
    public void Style_guide_is_embedded_and_extracts_the_bulgarian_prompt_block()
    {
        var styleGuide = PromptLibrary.EditorialStyleGuide;

        Assert.False(string.IsNullOrWhiteSpace(styleGuide));
        Assert.Contains("Стилови правила", styleGuide);
    }

    [Fact]
    public void Extraction_strips_the_markers_and_surrounding_documentation()
    {
        var styleGuide = PromptLibrary.EditorialStyleGuide;

        Assert.DoesNotContain("PROMPT-START", styleGuide);
        Assert.DoesNotContain("PROMPT-END", styleGuide);
        // English notes outside the marked block are documentation, not prompt.
        Assert.DoesNotContain("Open points for the owner", styleGuide);
    }

    [Fact]
    public void Missing_markers_throw_a_descriptive_exception()
    {
        var withoutStart = Assert.Throws<InvalidOperationException>(
            () => PromptLibrary.ExtractPromptBlock("no markers at all"));
        Assert.Contains("PROMPT-START", withoutStart.Message);

        var withoutEnd = Assert.Throws<InvalidOperationException>(
            () => PromptLibrary.ExtractPromptBlock("<!-- PROMPT-START --> текст без край"));
        Assert.Contains("PROMPT-END", withoutEnd.Message);
    }

    [Fact]
    public void Empty_prompt_block_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => PromptLibrary.ExtractPromptBlock("<!-- PROMPT-START -->  \n <!-- PROMPT-END -->"));
    }
}
