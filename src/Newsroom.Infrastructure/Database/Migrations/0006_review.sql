-- 0006_review: nw_ReviewAction + nw_TelegramPending + review columns on nw_Draft
-- (docs/02-functional-spec.md §5, docs/05-integrations/telegram.md). Single batch, no GO.

-- Audit trail: every editor action on a draft, keyed by Telegram user (docs/05 telegram.md).
CREATE TABLE dbo.nw_ReviewAction (
    Id             bigint        NOT NULL IDENTITY(1,1) PRIMARY KEY,
    DraftId        bigint        NOT NULL REFERENCES dbo.nw_Draft(Id),
    TelegramUserId bigint        NOT NULL,
    UserName       nvarchar(200) NULL,
    [Action]       nvarchar(30)  NOT NULL,   -- 'Approved' | 'Rejected' | 'ChangesRequested'
    Comment        nvarchar(max) NULL,       -- e.g. the regeneration instructions
    AtUtc          datetime2     NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_nw_ReviewAction_DraftId ON dbo.nw_ReviewAction (DraftId);

-- One open conversation per (chat, user): the editor's next message answers the bot's
-- "опиши промените" question (Kind 'ChangeInstructions').
CREATE TABLE dbo.nw_TelegramPending (
    Id           bigint       NOT NULL IDENTITY(1,1) PRIMARY KEY,
    ChatId       bigint       NOT NULL,
    UserId       bigint       NOT NULL,
    DraftId      bigint       NOT NULL,
    Kind         nvarchar(30) NOT NULL,      -- 'ChangeInstructions'
    CreatedAtUtc datetime2    NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE UNIQUE INDEX UX_nw_TelegramPending_ChatId_UserId ON dbo.nw_TelegramPending (ChatId, UserId);

-- Review-surface state on the draft itself: the posted Telegram message, and — on regenerated
-- versions — the editor's instructions plus a link to the version they supersede.
-- (The poll offset lives in nw_Config under 'Telegram:UpdateOffset'.)
ALTER TABLE dbo.nw_Draft ADD
    TelegramMessageId bigint        NULL,
    RegenInstructions nvarchar(max) NULL,
    ParentDraftId     bigint        NULL;
