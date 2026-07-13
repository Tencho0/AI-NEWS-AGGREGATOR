-- 0011_topic_force_draft: ForceDraftAtUtc on nw_Topic — the editor's /draft <topic> command sets
-- this marker so DraftJob drafts the topic even when it is not Hot
-- (docs/05-integrations/telegram.md). Orthogonal to Status: the topic keeps its real lifecycle
-- state. DraftRepository.SaveDraftAsync clears the marker once a draft is produced, so a later
-- reject/expire of that draft does not silently re-trigger generation. Single batch, no GO.

ALTER TABLE dbo.nw_Topic ADD ForceDraftAtUtc datetime2 NULL;
