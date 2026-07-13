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

    private static TgText Text(
        string text, long userId = Editor, long chatId = ReviewChat, long? replyToMessageId = null) =>
        new(UpdateId: 2, UserId: userId, UserName: "ivan", ChatId: chatId, MessageId: 78, Text: text,
            ReplyToMessageId: replyToMessageId);

    private static TgPhoto Photo(long userId = Editor, long chatId = ReviewChat) =>
        new(UpdateId: 3, UserId: userId, UserName: "ivan", ChatId: chatId, MessageId: 79,
            FileId: "file-abc", ReplyToMessageId: 55);

    private static ReviewCommand RouteCallback(TgCallback c) =>
        ReviewUpdateRouter.RouteCallback(c, Allowed, ReviewChat);

    private static ReviewCommand RouteText(
        TgText t, long? pendingDraftId = null, long? draftIdFromReply = null) =>
        ReviewUpdateRouter.RouteText(t, Allowed, ReviewChat, pendingDraftId, draftIdFromReply);

    private static ReviewCommand RoutePhoto(TgPhoto p, long? draftIdFromReply) =>
        ReviewUpdateRouter.RoutePhoto(p, Allowed, ReviewChat, draftIdFromReply);

    [Fact]
    public void Callback_approve_reject_changes_route_to_typed_commands()
    {
        Assert.Equal(new ApproveDraft(42), RouteCallback(Callback("approve:42")));
        Assert.Equal(new RejectDraft(42), RouteCallback(Callback("reject:42")));
        Assert.Equal(new RequestChanges(42), RouteCallback(Callback("changes:42")));
    }

    [Fact]
    public void Image_callback_routes_to_CycleImage()
    {
        Assert.Equal(new CycleImage(42), RouteCallback(Callback("image:42")));
    }

    [Fact]
    public void Image_callback_is_gated_like_every_other_callback()
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonNotAllowlisted),
            RouteCallback(Callback("image:42", userId: Stranger)));
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonWrongChat),
            RouteCallback(Callback("image:42", chatId: OtherChat)));
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
    public void Reply_bound_instructions_prefer_the_replied_draft_over_the_pending_conversation()
    {
        // Two drafts await changes: the reply pins the instructions to card #7, not pending #42.
        var command = RouteText(
            Text("Смени заглавието.", replyToMessageId: 55), pendingDraftId: 42, draftIdFromReply: 7);

        Assert.Equal(new SubmitChangeInstructions(7, "Смени заглавието."), command);
    }

    [Fact]
    public void Reply_bound_text_without_pending_conversation_still_becomes_instructions()
    {
        var command = RouteText(
            Text("Добави източник.", replyToMessageId: 55), pendingDraftId: null, draftIdFromReply: 7);

        Assert.Equal(new SubmitChangeInstructions(7, "Добави източник."), command);
    }

    [Fact]
    public void Reply_bound_command_still_routes_as_command()
    {
        Assert.Equal(new ShowStatus(),
            RouteText(Text("/status", replyToMessageId: 55), pendingDraftId: 42, draftIdFromReply: 7));
    }

    [Fact]
    public void Photo_reply_to_a_review_card_becomes_AttachEditorPhoto()
    {
        Assert.Equal(new AttachEditorPhoto(7, "file-abc"), RoutePhoto(Photo(), draftIdFromReply: 7));
    }

    [Fact]
    public void Photo_without_draft_context_is_ignored()
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonNoDraftContext),
            RoutePhoto(Photo(), draftIdFromReply: null));
    }

    [Fact]
    public void Photo_from_non_allowlisted_user_is_ignored()
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonNotAllowlisted),
            RoutePhoto(Photo(userId: Stranger), draftIdFromReply: 7));
    }

    [Fact]
    public void Photo_from_wrong_chat_is_ignored_even_for_editors()
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonWrongChat),
            RoutePhoto(Photo(chatId: OtherChat), draftIdFromReply: 7));
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
    [InlineData("/help")]
    [InlineData("/HELP")]
    [InlineData("/help@PredelNewsBot")]
    public void Help_command_routes(string text)
    {
        Assert.Equal(new ShowHelp(), RouteText(Text(text)));
    }

    [Fact]
    public void Quota_and_health_commands_route()
    {
        Assert.Equal(new ShowQuota(), RouteText(Text("/quota")));
        Assert.Equal(new ShowHealth(), RouteText(Text("/health")));
    }

    [Fact]
    public void Unmute_parses_topic_id()
    {
        Assert.Equal(new UnmuteTopic(12), RouteText(Text("/unmute 12")));
        Assert.Equal(new UnmuteTopic(7), RouteText(Text("/unmute   7  "))); // tolerant spacing
    }

    [Theory]
    [InlineData("/unmute")]
    [InlineData("/unmute abc")]
    [InlineData("/unmute 0")]
    [InlineData("/unmute -3")]
    [InlineData("/unmute 12 extra")]
    public void Unmute_with_bad_arguments_is_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonBadArguments), RouteText(Text(text)));
    }

    [Fact]
    public void Draft_parses_topic_id()
    {
        Assert.Equal(new ForceDraftTopic(42), RouteText(Text("/draft 42")));
        Assert.Equal(new ForceDraftTopic(5), RouteText(Text("/draft   5  ")));
    }

    [Theory]
    [InlineData("/draft")]
    [InlineData("/draft 0")]
    [InlineData("/draft -1")]
    [InlineData("/draft https://example.com")] // URL form is Phase 4b — a non-numeric arg is bad args
    [InlineData("/draft 42 extra")]
    public void Draft_with_bad_arguments_is_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonBadArguments), RouteText(Text(text)));
    }

    [Theory]
    [InlineData("здравей")]
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

    [Fact]
    public void Post_splits_headline_and_body()
    {
        Assert.Equal(
            new CreateArticle("Заглавие", "Първи ред.\nВтори ред."),
            RouteText(Text("/post Заглавие\nПърви ред.\nВтори ред.")));
    }

    [Fact]
    public void Post_headline_may_start_on_the_next_line()
    {
        Assert.Equal(
            new CreateArticle("Заглавие", "Тялото на статията."),
            RouteText(Text("/post\nЗаглавие\nТялото на статията.")));
    }

    [Fact]
    public void Post_single_line_is_headline_only()
    {
        Assert.Equal(new CreateArticle("Само заглавие", ""), RouteText(Text("/post Само заглавие")));
    }

    [Fact]
    public void Post_normalizes_windows_line_endings()
    {
        Assert.Equal(
            new CreateArticle("Заглавие", "Тяло."),
            RouteText(Text("/post Заглавие\r\nТяло.")));
    }

    [Fact]
    public void New_keeps_line_breaks_in_the_editor_text()
    {
        Assert.Equal(
            new CreateAiArticle("бележка ред 1\nбележка ред 2"),
            RouteText(Text("/new бележка ред 1\nбележка ред 2")));
    }

    [Fact]
    public void Post_and_new_route_with_botname_suffix()
    {
        Assert.Equal(new CreateArticle("Заглавие", ""), RouteText(Text("/post@MyBot Заглавие")));
        Assert.Equal(new CreateAiArticle("бележки"), RouteText(Text("/new@MyBot бележки")));
    }

    [Theory]
    [InlineData("/post")]
    [InlineData("/post   ")]
    [InlineData("/new")]
    [InlineData("/new \n ")]
    public void Post_and_new_without_text_are_ignored(string text)
    {
        Assert.Equal(new Ignore(ReviewUpdateRouter.ReasonBadArguments), RouteText(Text(text)));
    }
}
