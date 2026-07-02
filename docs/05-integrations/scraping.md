# Integration — Scraping Strategy

**Status:** Draft · **Last updated:** 2026-07-02

## Principles

1. **RSS/Atom first.** Most Bulgarian news sites expose feeds; feeds are stable, cheap and polite.
   HTML scraping is the fallback, sitemap polling the middle ground.
2. **Per-source adapter, shared pipeline.** A source row selects a parser kind (`Kind`:
   `rss` | `sitemap` | `html`) with an optional extraction hint in `ParserHint`
   (`selector:<css>`); adding a source is data entry + at most a small selector config,
   not a redeploy.
3. **Be a polite citizen.** Respect `robots.txt`, send an honest `User-Agent`
   (`PredelNewsBot/1.0 (+https://predel.news/bot)`), conditional GETs (`ETag` /
   `If-Modified-Since`), per-host delay between page fetches (default ≥ 10 s); sources are
   processed sequentially (effective global concurrency of 1 at v1 volume).
4. **Store for analysis, never for republication.** Extracted text is an internal working copy
   used to understand the story; generated articles are original syntheses citing the sources.
   Scraped images are never reused (see images.md, 06-security.md).

## Extraction

- RSS: `System.ServiceModel.Syndication`; follow item link only if the feed body is truncated
  and full text is needed for analysis.
- HTML: AngleSharp with a readability-style heuristic (largest text block, boilerplate removal),
  plus optional per-source CSS selectors stored in `nw_Source.ParserHint`.
- Canonicalisation (v1): normalise the **feed link** — strip tracking params/fragment/default
  ports, sort query params (`UrlCanonicalizer`); hash normalized text (`ContentHash`) for dedup
  across sources republishing agency wire copy. Reading the page's `<link rel="canonical">` is a
  possible refinement once full-page fetches are common (not implemented).
- JS-rendered sources: **not supported in v1.** If a must-have source requires it, write an ADR
  before adding Playwright (heavy dependency on the VPS).

## Scheduling & failure behaviour

- Default poll interval 10 min per source; hot sources can go to 5 min, slow ones to 60.
- Per-source failures never block the run: log, set `LastError`, continue. Three consecutive
  failures → warning to the Telegram admin thread; source auto-disabled after 24 h of failures
  (re-enabled manually via `/status` follow-up or DB).
- HTTP: `Microsoft.Extensions.Http.Resilience` standard handler (Polly v8: retry with backoff,
  circuit breaker per named client, timeout); hard timeout 30 s. Per-host circuit breaking is
  covered in practice by per-source failure isolation + auto-disable at v1 volume.

## Implementation notes (v1)

Conventions fixed in the Phase 1 code (`src/Newsroom.Core/Scraping`,
`src/Newsroom.Infrastructure/Scraping`, `src/Newsroom.Worker/Jobs/ScrapeJob.cs`):

- **Source kinds:** only `rss` is implemented in v1. `sitemap` and `html` are declared in the
  model but record a "not supported yet" failure — implementing them is a later task.
- **`ParserHint` convention:** `selector:<css-selector>` (e.g. `selector:div.article-body`)
  forces extraction from that CSS selector. Without a hint the extractor falls back to
  `[itemprop=articleBody]` / `article` / `main`, then the densest `<p>` cluster.
- **Identity:** User-Agent is exactly `PredelNewsBot/1.0 (+https://predel.news/bot)`
  (config `Scrape:UserAgent`); the robots.txt group token is `predelnewsbot` — a
  `User-agent: predelnewsbot` group beats `*`, longest-match rule wins, `robots.txt` 404
  fails open, per-host cache 24 h.
- **Conditional GET state** lives on `nw_Source`: `Etag`, `LastModifiedHeader`;
  `LastSuccessAtUtc` tracks source health.
- **Auto-disable rule:** `ConsecutiveFailures >= 3` **and** no success within the last
  `Scrape:DisableAfterFailingHours` (default 24) → `Enabled = 0` + warning log. The Telegram
  admin alert on top of this arrives with Phase 4; until then, watch the logs.
- **Download cap:** article page fetches are capped at 2 MB; full text is fetched only when the
  feed body is shorter than `Scrape:MinTextLength` (400) and `Scrape:FetchFullText` is on.
- **Seen-before skip:** already-stored URLs (batch-checked by hash) are skipped without any page
  fetch. Consequence: post-publication edits are only tracked for feeds carrying full text
  (`content:encoded`); truncated-feed articles are frozen at first sight — acceptable because
  analysis happens shortly after first sight.
- **Resilience:** `IHttpClientFactory` + the `Microsoft.Extensions.Http.Resilience` standard
  handler (retry, circuit breaker, timeout) instead of hand-rolled Polly policies; 30 s timeout,
  max 5 redirects.

Adding a source = following [`../runbooks/add-a-source.md`](../runbooks/add-a-source.md).

## Initial source list (Q-1 — to be confirmed by the editor)

To be filled in `nw_Source` at Phase 1; candidates: BTA (bta.bg), regional Blagoevgrad outlets,
national outlets with regional sections, municipality press pages. Selection criteria: relevance
to Southwest Bulgaria, feed availability, terms of use allowing indexing/monitoring.

## Legal note

Monitoring publicly available news for analysis is standard practice, but each source's ToS should
be checked when added (checkbox in the source-onboarding checklist). We do not bypass paywalls or
authentication, ever. See risk R-3.
