-- 0013_facebook_caption_scheduling: social-native Facebook posting
-- (docs/superpowers/specs/2026-07-17-facebook-engagement-design.md).
-- FacebookCaption + FacebookHashtagsJson: the AI-written social caption (hook / CTA / hashtags)
-- posted to the page instead of the ALL-CAPS headline + full body; NULL = legacy draft, the old
-- composition applies. ScheduledForUtc: 📅 Насрочи gate — an Approved draft with a future value
-- waits; NULL or past = publish on the next cycle. Single batch, no GO.

ALTER TABLE dbo.nw_Draft ADD
    FacebookCaption      nvarchar(1200) NULL,
    FacebookHashtagsJson nvarchar(400)  NULL,
    ScheduledForUtc      datetime2      NULL;
