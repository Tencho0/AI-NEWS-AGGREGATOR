using Newsroom.Core.Review;

namespace Newsroom.Core.Tests.Review;

public class ReviewUpdateRouterTests
{
    private const long Editor = 100;
    private const long Stranger = 666;
    private const long ReviewChat = -500;
    private const long OtherChat = -900;

    private static readonly IReadOnlySet<long> Allowed = new HashSet<long> { Editor };

    private static TgCallback Callback(string data, long userId = Editor, long chatId = ReviewChat) =>
        new(UpdateId: 1, CallbackId: "cb-1", UserId: userId, UserName: "ivan", ChatId: chatId,
            MessageId: 77, Data: data);

    private static TgText Text(string text, long userId = Editor, long chatId = ReviewChat) =>
        new(UpdateId: 2, UserId: userId, UserName: "ivan", ChatId: chatId, MessageId: 78, Text: text);

    private static ReviewCommand RouteCallback(TgCallback c) =>
        ReviewUpdateRouter.RouteCallback(c, Allowed, ReviewChat);

    private static ReviewCommand RouteText(TgText t, long? pendingDraftId = null) =>
        ReviewUpdateRouter.RouteText(t, Allowed, ReviewChat, pendingDraftId);

    [Fact]
    public void Callback_approve_reject_changes_route_to_typed_commands()
    {
        Assert.Equal(new ApproveDraft(42), RouteCallback(Callback("approve:42")));
        Assert.Equal(new RejectDraft(42), RouteCallback(Callback("reject:42")));
        Assert.Equal(new RequestChanges(42), RouteCallback(Callback("changes:42")));
    }

    [Fact]
    public void Callback_from_non_allowlisted_user_is_ignored_with_the_toastable_reason()
    {
        var command = RouteCallback(Callback("approve:42", userId: Stranger));

        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonNotAllowlisted), command);
    }

    [Fact]
    public void Callback_from_wrong_chat_is_ignored_silently_even_for_editors()
    {
        var command = RouteCallback(Callback("approve:42", chatId: OtherChat));

        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonWrongChat), command);
    }

    [Theory]
    [InlineData("publish:42")]
    [InlineData("approve")]
    [InlineData("approve:")]
    [InlineData("approve:abc")]
    [InlineData("approve:-5")]
    [InlineData(":42")]
    [InlineData("")]
    public void Callback_with_unknown_or_malformed_data_is_ignored(string data)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonUnknownData), RouteCallback(Callback(data)));
    }

    [Fact]
    public void Text_during_pending_conversation_becomes_change_instructions()
    {
        var command = RouteText(Text("  Съкрати и добави цитат от кмета.  "), pendingDraftId: 42);

        Assert.Equal(new SubmitChangeInstructions(42, "Съкрати и добави цитат от кмета."), command);
    }

    [Fact]
    public void Command_during_pending_conversation_still_routes_as_command()
    {
        Assert.Equal(new ShowStatus(), RouteText(Text("/status"), pendingDraftId: 42));
    }

    [Fact]
    public void Text_from_non_allowlisted_user_is_ignored_even_with_pending_conversation()
    {
        var command = RouteText(Text("инструкции", userId: Stranger), pendingDraftId: 42);

        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonNotAllowlisted), command);
    }

    [Fact]
    public void Text_from_wrong_chat_is_ignored()
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonWrongChat),
            RouteText(Text("/status", chatId: OtherChat)));
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/STATUS")]
    [InlineData("/status@PredelNewsBot")]
    public void Status_command_routes_case_insensitively_and_with_bot_suffix(string text)
    {
        Assert.Equal(new ShowStatus(), RouteText(Text(text)));
    }

    [Fact]
    public void Topics_pause_and_resume_commands_route()
    {
        Assert.Equal(new ShowTopics(), RouteText(Text("/topics")));
        Assert.Equal(new PauseDrafting(), RouteText(Text("/pause")));
        Assert.Equal(new ResumeDrafting(), RouteText(Text("/resume")));
    }

    [Fact]
    public void Mute_parses_topic_id_with_default_and_explicit_hours()
    {
        Assert.Equal(new MuteTopic(12, 24), RouteText(Text("/mute 12")));
        Assert.Equal(new MuteTopic(12, 48), RouteText(Text("/mute 12 48")));
        Assert.Equal(new MuteTopic(7, 24), RouteText(Text("/mute   7  "))); // tolerant spacing
    }

    [Theory]
    [InlineData("/mute")]
    [InlineData("/mute abc")]
    [InlineData("/mute 0")]
    [InlineData("/mute -3")]
    [InlineData("/mute 12 xx")]
    [InlineData("/mute 12 0")]
    [InlineData("/mute 12 -1")]
    [InlineData("/mute 12 24 extra")]
    public void Mute_with_bad_arguments_is_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonBadArguments), RouteText(Text(text)));
    }

    [Theory]
    [InlineData("здравей")]
    [InlineData("/draft https://example.com")] // Phase 4b command — not routed yet
    [InlineData("/unknown")]
    [InlineData("   ")]
    public void Plain_chatter_and_unknown_commands_are_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonUnknownText), RouteText(Text(text)));
    }

    [Fact]
    public void Plain_text_without_pending_conversation_is_not_instructions()
    {
        var command = RouteText(Text("Това е просто съобщение."), pendingDraftId: null);

        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonUnknownText), command);
    }
}
