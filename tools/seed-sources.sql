-- =============================================================================
-- seed-sources.sql — production seed for dbo.nw_Source
--
-- Q-1 resolved by the owner on 2026-07-03. Feeds verified with the bot UA the
-- same day (see docs/decision-log.md). Idempotent: every insert is guarded by
-- IF NOT EXISTS on Url — safe to re-run.
--
-- Deferred (no usable RSS as of 2026-07-03; need the sitemap/html adapters
-- from the backlog):
--   pirinsko.com      — HTML only, no feed discovered
--   blagoevgrad.bg    — municipality site; sitemap.xml only
--
-- Columns (schema: migrations 0001/0002):
--   Kind                    'rss' only in v1
--   ParserHint              NULL, or 'selector:<css>' when feed text is truncated
--                           and default extraction picks the wrong block
--   IntervalMinutes         10 primary, 15 secondary
--   PolitenessDelaySeconds  delay between full-text page fetches (>= 10)
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM dbo.nw_Source WHERE Url = N'https://www.bta.bg/bg/rss/free')
    INSERT INTO dbo.nw_Source (Name, Kind, Url, ParserHint, IntervalMinutes, Enabled, PolitenessDelaySeconds)
    VALUES (N'БТА (свободна лента)', N'rss', N'https://www.bta.bg/bg/rss/free', NULL, 10, 1, 10);

IF NOT EXISTS (SELECT 1 FROM dbo.nw_Source WHERE Url = N'https://www.mediapool.bg/rss')
    INSERT INTO dbo.nw_Source (Name, Kind, Url, ParserHint, IntervalMinutes, Enabled, PolitenessDelaySeconds)
    VALUES (N'Mediapool', N'rss', N'https://www.mediapool.bg/rss', NULL, 15, 1, 10);

IF NOT EXISTS (SELECT 1 FROM dbo.nw_Source WHERE Url = N'https://struma.bg/feed')
    INSERT INTO dbo.nw_Source (Name, Kind, Url, ParserHint, IntervalMinutes, Enabled, PolitenessDelaySeconds)
    VALUES (N'Струма', N'rss', N'https://struma.bg/feed', NULL, 10, 1, 10);

IF NOT EXISTS (SELECT 1 FROM dbo.nw_Source WHERE Url = N'https://toppresa.com/feed')
    INSERT INTO dbo.nw_Source (Name, Kind, Url, ParserHint, IntervalMinutes, Enabled, PolitenessDelaySeconds)
    VALUES (N'Топ Преса', N'rss', N'https://toppresa.com/feed', NULL, 15, 1, 10);

IF NOT EXISTS (SELECT 1 FROM dbo.nw_Source WHERE Url = N'https://infomreja.bg/rss')
    INSERT INTO dbo.nw_Source (Name, Kind, Url, ParserHint, IntervalMinutes, Enabled, PolitenessDelaySeconds)
    VALUES (N'ИнфоМрежа', N'rss', N'https://infomreja.bg/rss', NULL, 15, 1, 10);

IF NOT EXISTS (SELECT 1 FROM dbo.nw_Source WHERE Url = N'https://www.blagoevgrad24.bg/rss/')
    INSERT INTO dbo.nw_Source (Name, Kind, Url, ParserHint, IntervalMinutes, Enabled, PolitenessDelaySeconds)
    VALUES (N'Благоевград24', N'rss', N'https://www.blagoevgrad24.bg/rss/', NULL, 10, 1, 10);

-- Post-seed sanity check:
SELECT Id, Name, Kind, Url, IntervalMinutes, Enabled, PolitenessDelaySeconds
FROM dbo.nw_Source ORDER BY Id;
