-- 0012_manual_topics: EditorInput on nw_Topic — editor-authored articles (/post, /new;
-- docs/05-integrations/telegram.md). Topics with Status='Manual' are synthetic: created by the
-- Telegram commands, never by trend detection; EditorInput is the editor's original text and is
-- the "source article" the drafting AI (and self-check) works from. Single batch, no GO.

ALTER TABLE dbo.nw_Topic ADD EditorInput nvarchar(max) NULL;
