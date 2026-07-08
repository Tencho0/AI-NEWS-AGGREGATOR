-- 0010_review_posted_at: PostedAtUtc on nw_Draft — anchors the review-TTL sweep to when the
-- review card was actually posted to Telegram, not when the draft row was created.
-- A ✏️ regeneration reuses the SAME row (see DraftRepository.CompleteRegenerationAsync) and can
-- sit in Generating for a long time under free-tier quota stalls; expiring on CreatedAtUtc then
-- gave the editor a shrunken — sometimes zero — review window on the freshly posted new version
-- (found live 2026-07-03). Single batch, no GO.

ALTER TABLE dbo.nw_Draft ADD PostedAtUtc datetime2 NULL;

-- Backfill drafts already posted before this migration so they keep expiring on their original
-- clock (CreatedAtUtc) rather than becoming non-expirable — the sweep excludes PostedAtUtc NULL.
-- Dynamic SQL: the column is added above but does not exist at batch-compile time, so a direct
-- UPDATE would fail to compile in this single (no-GO) batch.
EXEC(N'UPDATE dbo.nw_Draft SET PostedAtUtc = CreatedAtUtc WHERE TelegramMessageId IS NOT NULL AND PostedAtUtc IS NULL;');
