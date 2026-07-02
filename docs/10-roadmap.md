# 10 — Roadmap: Phases, Milestones, Task Breakdown

**Status:** Draft · **Last updated:** 2026-07-02

Durations are effort estimates for one developer part-time; adjust after Phase 1 (velocity data).
Each phase ends with: demo of the milestone criterion, docs updated, progress log entry below.
**Detailed per-phase task plans are written at the start of each phase** (documentation-first) —
the breakdowns here are the seed, not the final word.

## Phase 0 — Foundations *(~2–3 days)*

Goal: repo + docs + walking skeleton.
- [x] Documentation tree (this) — review and confirm ADRs 0002–0009 with the owner
- [ ] Resolve blocking open questions: Q-1 sources, Q-2 style guide (Q-3 deferred to Phase 3)
- [x] Solution scaffold (`Newsroom.Core/Infrastructure/Worker` + Infrastructure tests), CI build
      (`Newsroom.Core.Tests` deferred until Core gains its first logic in Phase 2)
- [x] `Newsroom` DB + migration runner + first migration (`nw_Source`, `nw_Config`, `nw_Log`)
- [x] Serilog wiring; Windows-Service-capable host runs and heartbeats
- **Milestone M0:** ✅ 2026-07-02 — worker runs on dev (default SQL instance), creates the DB,
  applies migration 0001 (idempotent on re-run), heartbeats to `nw_Config`, logs to console +
  rolling file; 12 unit tests green; CI workflow in place. Service-install on the VPS is Phase 7.

## Phase 1 — Scraping & storage *(~1 week)*

- [x] Source model + repositories (`nw_Source` from Phase 0, migration `0002_source_articles`
      adds `nw_SourceArticle` + conditional-GET/health columns); the admin **seed** itself is
      blocked on Q-1 — template ready in `tools/seed-sources.sql`, onboarding steps in
      `runbooks/add-a-source.md`
- [x] RSS adapter; HTML adapter (AngleSharp + readability heuristic); canonical URL + hash dedup
- [x] Politeness layer (robots.txt, conditional GET, per-host delay); resilience via the
      `Microsoft.Extensions.Http.Resilience` standard handler (retry/circuit breaker/timeout)
- [x] `ScrapeJob` on schedule; per-source failure isolation + auto-disable
- [ ] Fixture-based extraction tests for every launch source — generic extraction/canonicalisation/
      robots fixtures exist; per-launch-source fixtures await the confirmed source list (Q-1)
- **Milestone M1:** 48 h unattended run collecting the real sources with zero duplicate rows.

## Phase 2 — Analysis & trend detection *(~1 week)*

- [ ] AI layer: `IAiClient` over `Microsoft.Extensions.AI.IChatClient`, Gemini adapter
      (`Google.GenAI`), per-stage provider/model config, RPM throttle, `nw_CostLedger` +
      daily request/cost budgets (ADR-0010)
- [ ] `AnalyseJob`: summarise/classify on Gemini Flash, multi-article request packing,
      structured outputs (JSON schema)
- [ ] Clustering + trend scoring; `TrendJob`; tunable threshold
- [ ] Backtest against the Phase-1 corpus; tune scoring
- **Milestone M2:** system flags hot topics that match what an editor would pick (spot-check on a
  week of real data), at measured cost per article.

## Phase 3 — Draft generation & images *(~1–1.5 weeks)*

- [ ] Prompt v1 (style guide baked in); drafting on Gemini Flash with structured output —
      golden-set eval decides whether Flash quality suffices or a stage upgrade is needed (R-12)
- [ ] Validation gates (schema, taxonomy, self-check) + `GenerationFailed` path
- [ ] Image service: media-library search + Pexels/Pixabay + attribution + alt text
- [ ] Golden-set eval round with the editor; prompt v2
- **Milestone M3:** given a hot topic, a publishable-quality Bulgarian draft + images exists in
  the DB in < 5 min; editor rubric average ≥ agreed bar.

## Phase 4 — Telegram review loop *(~1 week)*

- [ ] Bot, long polling, allowlist, `nw_TelegramState`
- [ ] Review message + inline keyboard; approve/reject; TTL expiry
- [ ] Change-request conversation → regeneration versions; image cycling + editor upload
- [ ] Commands `/status /draft /topics /mute /pause /resume`; admin alerts
- **Milestone M4:** full review lifecycle exercised on real drafts in the editorial chat
  (publishing still stubbed).

## Phase 5 — Umbraco publishing *(~1 week, touches both repos)*

- [ ] Publishing endpoint in Predel-News (`PublishingApiController` + API user) per contract
- [ ] Worker `UmbracoPublisher` + idempotency + retries; contract tests both sides
- [ ] `PublishJob` for approved drafts; `PublishRecord`; Telegram confirmation with live link
- **Milestone M5:** approved draft appears correctly on the (dev, then live) site — slug,
  category, image, SEO fields all right — with no manual steps.

## Phase 6 — Facebook publishing *(~0.5–1 week + Meta review lead time)*

- [ ] (Started in Phase 0!) Meta app, business verification, app review, long-lived Page token
- [ ] Link-post publishing after Umbraco success; teaser text generation; DryRun flag
- [ ] Token health check; `PartiallyPublished` retry flow; group-share helper text in confirmation
- **Milestone M6:** approval → article live on site **and** page, end to end, < 2 min.

## Phase 7 — Hardening & production *(~1 week)*

- [ ] Watchdog/heartbeat alerts, daily digest, crash-recovery resets, retention pruning
- [ ] Deploy script, service install on VPS, runbooks written, rollback rehearsed
- [ ] 1–2 week supervised production pilot with the editor; tune thresholds/prompts/TTL
- **Milestone M7 (v1.0):** two consecutive weeks of production operation where every published
  article went through the pipeline and no incident required RDP access to fix.

## Later (backlog, each needs an ADR when promoted)

Scheduled publishing · engagement feedback loop (FB/site stats → trend weights) · web review UI ·
additional distribution channels (Instagram via Graph API, newsletter) · full-auto mode for
low-risk categories · Playwright for JS-heavy sources · Quartz.NET if scheduling outgrows timers.

## Progress log

| Date | Entry |
|---|---|
| 2026-07-02 | Project plan + documentation tree created; ADRs 0001–0009 drafted (0002–0009 `Proposed`, awaiting owner confirmation). |
| 2026-07-02 | Owner decision: Gemini API (free tier) as default AI provider with easy provider switching → ADR-0005 rejected, ADR-0010 accepted; AI docs, risks and budgets reworked around free-tier quotas. |
| 2026-07-02 | Owner decision: Gemini only at launch — free-provider research parked (research/2026-07-free-ai-providers.md), Q-9 parked. |
| 2026-07-02 | **M0 reached.** Solution scaffolded (`Newsroom.slnx`: Core / Infrastructure / Worker / Infrastructure.Tests); migration runner + `0001_initial` (nw_Config, nw_Source, nw_Log, nw_SchemaVersion); Serilog console+file; Windows-Service-capable host with DB heartbeat; verified live: DB auto-created, migration applied once, idempotent re-run, heartbeats persisted. 12 tests green; GitHub Actions CI added. Dev note: local dev machine runs a default SQL instance (`Server=.` in appsettings.Development.json); VPS default stays `.\SQLEXPRESS`. |
| 2026-07-02 | **Phase 1 live-verified** on a temporary smoke source (deleted afterwards): 31 articles collected with real full-text extraction (2.5–7 k chars/article); repeated full refetches produced zero duplicates; two genuinely new mid-test items picked up incrementally. Found & fixed in the process: full text was fetched before the seen-before check → added batch URL-hash existence check so known articles cost no page fetch (politeness); consequence documented in scraping.md. 46 unit tests green. Remaining for M1: seed real sources (Q-1) and run 48 h unattended. |
| 2026-07-02 | **Phase 1 implemented** (code complete, M1 not yet reached): migration `0002_source_articles` (`nw_SourceArticle` with UrlHash/ContentHash dedup keys + Etag/LastModifiedHeader/LastSuccessAtUtc on `nw_Source`); scraping core (URL canonicaliser, content hash, feed/extractor/robots/repository interfaces); RSS reader with conditional GET; AngleSharp extractor with `ParserHint` CSS selectors + readability fallback; robots.txt policy (`predelnewsbot` token, 24 h cache); Dapper repositories with MERGE upsert (no duplicate canonical URLs); `ScrapeJob` with per-source failure isolation, politeness delay and auto-disable sweep. Implementation conventions recorded in 05-integrations/scraping.md; source onboarding runbook + seed template added. **Awaits:** Q-1 confirmed source list (seed + per-source fixtures) and the M1 48-hour unattended run. |
