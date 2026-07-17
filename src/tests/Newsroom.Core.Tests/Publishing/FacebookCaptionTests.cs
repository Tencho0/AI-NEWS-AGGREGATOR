using Newsroom.Core.Publishing;

namespace Newsroom.Core.Tests.Publishing;

public class FacebookCaptionTests
{
    [Fact]
    public void Compose_appends_hashtags_after_a_blank_line() =>
        Assert.Equal("Текст на поста.\n\n#Благоевград #ПределНюз",
            FacebookCaption.Compose("Текст на поста.", ["#Благоевград", "#ПределНюз"]));

    [Fact]
    public void Compose_without_hashtags_returns_the_trimmed_caption_alone() =>
        Assert.Equal("Текст на поста.", FacebookCaption.Compose("Текст на поста.\n", []));

    [Fact]
    public void Compose_skips_blank_hashtags() =>
        Assert.Equal("Текст.\n\n#Пирин", FacebookCaption.Compose("Текст.", ["", "#Пирин", " "]));
}
