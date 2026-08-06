-- 0016_publish_target: per-draft publish destination
-- (docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md).
-- 'Both' (website, then the link to Facebook) | 'Website' | 'Facebook', written at ✅ time by the
-- editor's chosen button. NOT NULL with a DEFAULT so every existing row — including drafts
-- sitting Approved or PartiallyPublished mid-deploy — backfills to the pre-feature behaviour and
-- nothing in flight changes meaning. Single batch, no GO.

ALTER TABLE dbo.nw_Draft ADD PublishTarget nvarchar(20) NOT NULL
    CONSTRAINT DF_nw_Draft_PublishTarget DEFAULT 'Both';
