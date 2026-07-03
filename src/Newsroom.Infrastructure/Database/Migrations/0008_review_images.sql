-- 0008_review_images: TelegramPhotoMessageId on nw_Draft for the Phase 4b image review
-- experience (docs/05-integrations/telegram.md). Single batch, no GO.

-- The photo message posted next to the text review card: 🖼 image cycling edits it in place
-- (editMessageMedia), and editor photo-upload replies are bound to a draft by matching either
-- this id or TelegramMessageId. NULL until the photo is dispatched — and forever for drafts
-- without stock suggestions, which keep the Phase 4a text-only flow.
ALTER TABLE dbo.nw_Draft ADD TelegramPhotoMessageId bigint NULL;
