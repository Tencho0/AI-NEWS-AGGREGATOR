# Runbook — Add a Source

**Status:** Agreed · **Last updated:** 2026-07-02

Source onboarding checklist promised by [07-operations.md](../07-operations.md). Adding a source
is data entry into `nw_Source` — no redeploy — but every step below must be done **in order**.
Only `Kind = 'rss'` sources are supported in v1 (see
[05-integrations/scraping.md](../05-integrations/scraping.md), "Implementation notes").

## 1. Verify the feed exists and parses

- Find the feed URL (look for `<link rel="alternate" type="application/rss+xml">` in the site's
  HTML head, or common paths `/rss`, `/feed`, `/rss.xml`).
- Fetch it and confirm it is valid RSS/Atom (root element `<rss>` or `<feed>`), e.g.:

  ```powershell
  Invoke-WebRequest -Uri "<FEED-URL>" -UserAgent "PredelNewsBot/1.0 (+https://predel.news/bot)" |
      Select-Object -ExpandProperty Content | Select-Object -First 1
  ```

- Check whether items carry full text (`content:encoded`) or only truncated descriptions —
  truncated feeds will trigger full-text page fetches (step 4 matters more).

## 2. Check ToS / robots stance — and record it

- Read the site's terms of use: does anything forbid indexing/monitoring? We never bypass
  paywalls or authentication (risk R-3).
- Fetch `https://<host>/robots.txt` and confirm neither `User-agent: predelnewsbot` nor
  `User-agent: *` disallows the feed path and typical article paths. (The worker enforces
  robots.txt at runtime anyway, but a disallowed source should simply not be added.)
- **Record the check** — one line in [decision-log.md](../decision-log.md):
  date, outlet, ToS verdict, robots verdict, who checked. A source without a recorded check
  must not be enabled.

## 3. Choose `IntervalMinutes` and `PolitenessDelaySeconds`

- `IntervalMinutes`: default **10**; hot sources (wire agencies) 5, slow sources
  (municipality pages) 30–60. A source is polled when
  `LastCrawledAtUtc + IntervalMinutes <= now`.
- `PolitenessDelaySeconds`: default **10** — the wait between consecutive full-text page
  fetches on the same source. Raise it for small sites; never lower it below 5 without reason.

## 4. Optional: `ParserHint`

Only needed when the feed body is truncated **and** the default extraction (readability
heuristic) picks up the wrong block. Convention: `selector:<css-selector>`, e.g.
`selector:div.article-body`. Leave `NULL` first; add a hint only after step 6 shows poor
extraction quality.

## 5. Insert the row

```sql
IF NOT EXISTS (SELECT 1 FROM dbo.nw_Source WHERE Url = N'<FEED-URL>')
    INSERT INTO dbo.nw_Source
        (Name, Kind, Url, ParserHint, IntervalMinutes, Enabled, PolitenessDelaySeconds)
    VALUES
        (N'<Outlet name>', N'rss', N'<FEED-URL>', NULL, 10, 1, 10);
```

For seeding several sources at once use the template [`tools/seed-sources.sql`](../../tools/seed-sources.sql).

## 6. Verify after adding

Within one poll interval (plus up to `Scrape:CheckSeconds` = 60 s):

1. **Logs** (`logs/newsroom-.log` or console): look for
   `Source <Name>: <N> item(s), <M> new`. A robots warning
   (`... feed URL is disallowed by robots.txt`) means step 2 was wrong — disable the source.
2. **Rows landed:**

   ```sql
   SELECT TOP 10 Title, Url, PublishedAtUtc, LEN(ExtractedText) AS TextLen
   FROM dbo.nw_SourceArticle
   WHERE SourceId = (SELECT Id FROM dbo.nw_Source WHERE Url = N'<FEED-URL>')
   ORDER BY FirstSeenAtUtc DESC;
   ```

   Sanity-check titles and `TextLen` (very short text on every row → consider a `ParserHint`,
   step 4).
3. **Health:**

   ```sql
   SELECT Name, Enabled, LastCrawledAtUtc, LastSuccessAtUtc, ConsecutiveFailures, LastError
   FROM dbo.nw_Source
   WHERE Url = N'<FEED-URL>';
   ```

   `LastError` must be `NULL` and `ConsecutiveFailures` 0. Remember the auto-disable rule:
   3 consecutive failures + no success for 24 h → `Enabled = 0` (re-enable manually after
   fixing the cause).
