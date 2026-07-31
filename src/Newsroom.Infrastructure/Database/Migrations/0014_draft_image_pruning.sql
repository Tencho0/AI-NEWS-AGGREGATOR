-- 0014_draft_image_pruning: marks a nw_DraftImage row whose local file the retention job has
-- deleted (ADR-0013). The row itself is kept — it is the audit trail of what the editor saw and
-- what was published — but the file is gone, so the job must not keep re-attempting it, and
-- readers must not treat the Url as resolvable. Single batch, no GO.

ALTER TABLE dbo.nw_DraftImage ADD FilePrunedAtUtc datetime2 NULL;
