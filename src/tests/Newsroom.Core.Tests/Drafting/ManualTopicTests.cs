using Newsroom.Core.Drafting;

namespace Newsroom.Core.Tests.Drafting;

public class ManualTopicTests
{
    [Fact]
    public void Short_text_is_the_label()
    {
        Assert.Equal("Кметът откри новата зала", ManualTopic.LabelFrom("Кметът откри новата зала"));
    }

    [Fact]
    public void Only_the_first_nonempty_line_is_used()
    {
        Assert.Equal("Заглавие", ManualTopic.LabelFrom("\n  Заглавие  \nВтори ред с още текст."));
    }

    [Fact]
    public void Long_lines_truncate_on_a_word_boundary_with_ellipsis()
    {
        const string source =
            "Общинската администрация в Благоевград съобщи за нови мерки срещу задръстванията в центъра на града";

        var label = ManualTopic.LabelFrom(source);

        Assert.True(label.Length <= 60, $"label too long: {label.Length}");
        Assert.EndsWith("…", label);
        var prefix = label[..^1];
        Assert.StartsWith(prefix, source);        // no characters invented
        Assert.Equal(' ', source[prefix.Length]); // cut exactly at a word boundary, not mid-word
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        Assert.Equal("Заглавие", ManualTopic.LabelFrom("Заглавие\r\nТяло."));
    }
}
