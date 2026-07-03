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

- [x] AI layer: `IAiClient` over `Microsoft.Extensions.AI.IChatClient`, Gemini adapter
      (`Google.GenAI` official `AsIChatClient`), per-stage provider/model config, RPM throttle
      (SlidingWindowRateLimiter), `nw_CostLedger` + daily request budgets (ADR-0010)
- [x] `AnalyseJob`: summarise/classify on Gemini Flash, multi-article request packing,
      structured JSON output with robust parsing; poison protection via `AnalysisAttempts`;
      degrades to no-op with a warning when no API key is configured
- [x] Clustering + trend scoring; `TrendJob`; tunable threshold (`Trend:*` config; pure
      `TrendScorer` + `GeminiClusteringAi` on the shared seam; wire-copy pre-pass; shared
      process-wide RPM limiter across AI stages)
- [ ] Backtest & tune scoring — needs a real corpus: blocked on Q-1 sources + a week of collection
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
| 2026-07-03 | **Phase 2b implemented and live-verified** (clustering + trend scoring; migration `0004_topics`). Live chain on Mediapool: 15 articles → analysed → clustered into 14 topics with correct concise Bulgarian story labels; one cross-article join proved story grouping; 2-article topic scored 5.28 (hand-verified against the formula), correctly below the Hot threshold. Per-stage ledger: Cluster requests ~4× cheaper than Analyse. BTA persistently 429'd → failure isolation + auto-disable path exercised; source-level 429 cooldown queued for Phase 7 (scraping.md). 61 tests green. M2 remaining: backtest/tuning on a real corpus (blocked on Q-1). |
| 2026-07-02 | **Phase 2a implemented and live-verified.** AI layer per ADR-0010 (Gemini via official `Google.GenAI` `AsIChatClient` adapter behind `IAiClient`; RPM throttle; daily request budget in `nw_CostLedger`; migration `0003_analysis`) + `AnalyseJob`. Live E2E on the BTA free feed: 20 Bulgarian articles scraped → 3 Gemini batches (`gemini-2.5-flash`, 13,241 in / 4,972 out tokens, $0 free tier) → 20 correct Bulgarian summaries with sensible categories and region scores; a transient BTA 429 on first fetch was absorbed by retry/failure-isolation and recovered on the next cycle. Gemini key provisioned via `dotnet user-secrets` (dev; never in repo — see 06-security.md; rotate when convenient). Log noise fix: `System.Net.Http.HttpClient`/`Polly` Serilog overrides. 51 tests green. Remaining for M2: clustering + trend scoring (`TrendJob`) and backtest. |
| 2026-07-02 | **Phase 1 live-verified** on a temporary smoke source (deleted afterwards): 31 articles collected with real full-text extraction (2.5–7 k chars/article); repeated full refetches produced zero duplicates; two genuinely new mid-test items picked up incrementally. Found & fixed in the process: full text was fetched before the seen-before check → added batch URL-hash existence check so known articles cost no page fetch (politeness); consequence documented in scraping.md. 46 unit tests green. Remaining for M1: seed real sources (Q-1) and run 48 h unattended. |
| 2026-07-02 | **Phase 1 implemented** (code complete, M1 not yet reached): migration `0002_source_articles` (`nw_SourceArticle` with UrlHash/ContentHash dedup keys + Etag/LastModifiedHeader/LastSuccessAtUtc on `nw_Source`); scraping core (URL canonicaliser, content hash, feed/extractor/robots/repository interfaces); RSS reader with conditional GET; AngleSharp extractor with `ParserHint` CSS selectors + readability fallback; robots.txt policy (`predelnewsbot` token, 24 h cache); Dapper repositories with MERGE upsert (no duplicate canonical URLs); `ScrapeJob` with per-source failure isolation, politeness delay and auto-disable sweep. Implementation conventions recorded in 05-integrations/scraping.md; source onboarding runbook + seed template added. **Awaits:** Q-1 confirmed source list (seed + per-source fixtures) and the M1 48-hour unattended run. |
