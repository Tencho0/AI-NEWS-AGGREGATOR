# Integration — Scraping Strategy

**Status:** Draft · **Last updated:** 2026-07-02

## Principles

1. **RSS/Atom first.** Most Bulgarian news sites expose feeds; feeds are stable, cheap and polite.
   HTML scraping is the fallback, sitemap polling the middle ground.
2. **Per-source adapter, shared pipeline.** A source row selects a parser strategy
   (`rss`, `sitemap`, `html:<hint>`); adding a source is data entry + (only for HTML) a small
   selector config, not a redeploy where avoidable.
3. **Be a polite citizen.** Respect `robots.txt`, send an honest `User-Agent`
   (`PredelNewsBot/1.0 (+https://predel.news/bot)`), conditional GETs (`ETag` /
   `If-Modified-Since`), per-host delay (default ≥ 10 s between requests), global concurrency cap.
4. **Store for analysis, never for republication.** Extracted text is an internal working copy
   used to understand the story; generated articles are original syntheses citing the sources.
   Scraped images are never reused (see images.md, 06-security.md).

## Extraction

- RSS: `System.ServiceModel.Syndication`; follow item link only if the feed body is truncated
  and full text is needed for analysis.
- HTML: AngleSharp with a readability-style heuristic (largest text block, boilerplate removal),
  plus optional per-source CSS selectors stored in `nw_Source.ParserHint`.
- Canonicalisation: prefer `<link rel="canonical">`; strip tracking params; hash normalized text
  (`ContentHash`) for dedup across sources republishing agency wire copy.
- JS-rendered sources: **not supported in v1.** If a must-have source requires it, write an ADR
  before adding Playwright (heavy dependency on the VPS).

## Scheduling & failure behaviour

- Default poll interval 10 min per source; hot sources can go to 5 min, slow ones to 60.
- Per-source failures never block the run: log, set `LastError`, continue. Three consecutive
  failures → warning to the Telegram admin thread; source auto-disabled after 24 h of failures
  (re-enabled manually via `/status` follow-up or DB).
- HTTP: Polly retry (3 attempts, exponential + jitter) for transient codes; circuit breaker per
  host; hard timeout 30 s.

## Initial source list (Q-1 — to be confirmed by the editor)

To be filled in `nw_Source` at Phase 1; candidates: BTA (bta.bg), regional Blagoevgrad outlets,
national outlets with regional sections, municipality press pages. Selection criteria: relevance
to Southwest Bulgaria, feed availability, terms of use allowing indexing/monitoring.

## Legal note

Monitoring publicly available news for analysis is standard practice, but each source's ToS should
be checked when added (checkbox in the source-onboarding checklist). We do not bypass paywalls or
authentication, ever. See risk R-3.
