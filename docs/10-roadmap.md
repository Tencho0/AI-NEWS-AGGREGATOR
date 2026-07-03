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

- [x] Prompt v1 (style guide baked in — `docs/editorial-style-guide.md` single-sourced into Core
      as an embedded resource, PROMPT-START/END block); drafting on Gemini Flash with structured
      output (`GeminiDraftingAi`, stages Draft + SelfCheck)
- [x] Validation gates (`DraftValidator`: length/category/region/Cyrillic-ratio/SEO bounds) +
      `GenerationFailed` path with `DraftAttempts` cap
- [x] Image service: Pexels/Pixabay providers (key-optional degradation) + attribution + alt text;
      media-library tier deferred to Phase 5 (needs Umbraco access) — **API keys not yet
      provisioned** (owner: pixabay.com/api / pexels.com/api → user-secrets)
- [ ] Golden-set eval round with the editor; prompt v2 — needs the real corpus (Q-1) + owner
      review of the style guide (Q-2 draft written, 4 open points)
- **Milestone M3:** given a hot topic, a publishable-quality Bulgarian draft + images exists in
  the DB in < 5 min; editor rubric average ≥ agreed bar.

## Phase 4 — Telegram review loop *(~1 week)*

- [x] Bot, long polling, allowlist, poll offset + pending conversations persisted
      (`nw_Config`/`nw_TelegramPending`; migration 0006) — **dormant until the owner provisions
      Telegram:BotToken / ReviewChatId / AllowedUserIds (Q-8)**
- [x] Review message (HTML, escaped, truncated preview) + inline keyboard ✅/✏️/❌;
      approve/reject with audit (`nw_ReviewAction`); TTL expiry sweep
- [x] Change-request conversation → `Superseded` + regeneration version via DraftJob
      (image cycling + editor photo upload → Phase 4b)
- [ ] Commands: `/status /topics /mute /pause /resume` done; `/draft` force-draft, full-text
      attachment and admin alert routing → Phase 4b
- **Milestone M4:** ✅ 2026-07-03 — full review lifecycle exercised live in the editor's chat on
  real drafts: card delivery, ✅ approve (audited), ✏️ change request → regeneration honoring the
  editor's instruction («Намали на половина»: 2 252 → 973 chars) → new version re-delivered.
  Q-8 resolved: @PredelNewsBot, personal chat, one approver (credentials in user-secrets).

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
| 2026-07-03 | **M4 reached — live review loop verified with the editor.** @PredelNewsBot provisioned (Q-8 resolved); ✅ approve path: card → tap → Approved + `nw_ReviewAction` audit + message edit. ✏️ path: instruction «Намали на половина» → Superseded → regeneration → 973-char v2 delivered (57 % shorter). Live testing surfaced and fixed: (1) SEO title/description overflow hard-failed drafts → `DraftValidator.Normalize` auto-truncates at word boundaries; (2) Gemini free-tier quota exhaustion (R-11 materialized) → stages moved to `gemini-3.5-flash` (current gen, fresh per-model quota) and quota errors no longer consume retry attempts; (3) silent regeneration failures → bot now reports them to the chat; (4) tooling incident: PS 5.1 file rewrite corrupted UTF-8 Cyrillic in config → restored from git, re-applied safely, lesson recorded. 150 tests green. |
| 2026-07-03 | **Phase 4a implemented** (Telegram review loop; migration `0006_review`): review cards (HTML, escaped, inline ✅/✏️/❌), approve/reject with `nw_ReviewAction` audit, change-request conversation → `Superseded` + regeneration version through DraftJob, TTL expiry, `/status /topics /mute /pause /resume` (pause = DB runtime flag read by DraftJob), long polling with persisted offset, allowlist authorization. Telegram.Bot 22.10.1. Runs **dormant without credentials** — verified live: migration applied, exactly one "disabled: not configured" warning, zero errors. Implementation agent crashed mid-run (connection loss) and was resumed to completion — final state independently verified. 148 tests green. **M4 needs Q-8: bot token + review chat id + approver user ids, then a live review pass.** Phase 4b backlog: full-text attachment, image cycling, editor photo upload, `/draft`, admin alert routing. |
| 2026-07-03 | **Phase 3 implemented and live-verified** (migration `0005_drafts`; style guide drafted from the owner's sample articles and embedded as the prompt source; `GeminiDraftingAi` + `DraftValidator` + self-check; Pexels/Pixabay providers; `DraftJob`). Live E2E: Mediapool scrape → analyse → cluster → topic promoted Hot → **first real draft in 90 s**: ALL-CAPS two-part headline per house style, ~350-word body with attribution and concrete facts, category Политика, confidence 0.9, self-check correctly flagged the disputed claim, both source URLs recorded, status PendingReview. 0 image suggestions (Pixabay/Pexels keys pending — graceful degradation confirmed). 105 tests green. Remaining for M3: golden-set eval with the editor (Q-1/Q-2 review). |
| 2026-07-03 | **Phase 2b implemented and live-verified** (clustering + trend scoring; migration `0004_topics`). Live chain on Mediapool: 15 articles → analysed → clustered into 14 topics with correct concise Bulgarian story labels; one cross-article join proved story grouping; 2-article topic scored 5.28 (hand-verified against the formula), correctly below the Hot threshold. Per-stage ledger: Cluster requests ~4× cheaper than Analyse. BTA persistently 429'd → failure isolation + auto-disable path exercised; source-level 429 cooldown queued for Phase 7 (scraping.md). 61 tests green. M2 remaining: backtest/tuning on a real corpus (blocked on Q-1). |
| 2026-07-02 | **Phase 2a implemented and live-verified.** AI layer per ADR-0010 (Gemini via official `Google.GenAI` `AsIChatClient` adapter behind `IAiClient`; RPM throttle; daily request budget in `nw_CostLedger`; migration `0003_analysis`) + `AnalyseJob`. Live E2E on the BTA free feed: 20 Bulgarian articles scraped → 3 Gemini batches (`gemini-2.5-flash`, 13,241 in / 4,972 out tokens, $0 free tier) → 20 correct Bulgarian summaries with sensible categories and region scores; a transient BTA 429 on first fetch was absorbed by retry/failure-isolation and recovered on the next cycle. Gemini key provisioned via `dotnet user-secrets` (dev; never in repo — see 06-security.md; rotate when convenient). Log noise fix: `System.Net.Http.HttpClient`/`Polly` Serilog overrides. 51 tests green. Remaining for M2: clustering + trend scoring (`TrendJob`) and backtest. |
| 2026-07-02 | **Phase 1 live-verified** on a temporary smoke source (deleted afterwards): 31 articles collected with real full-text extraction (2.5–7 k chars/article); repeated full refetches produced zero duplicates; two genuinely new mid-test items picked up incrementally. Found & fixed in the process: full text was fetched before the seen-before check → added batch URL-hash existence check so known articles cost no page fetch (politeness); consequence documented in scraping.md. 46 unit tests green. Remaining for M1: seed real sources (Q-1) and run 48 h unattended. |
| 2026-07-02 | **Phase 1 implemented** (code complete, M1 not yet reached): migration `0002_source_articles` (`nw_SourceArticle` with UrlHash/ContentHash dedup keys + Etag/LastModifiedHeader/LastSuccessAtUtc on `nw_Source`); scraping core (URL canonicaliser, content hash, feed/extractor/robots/repository interfaces); RSS reader with conditional GET; AngleSharp extractor with `ParserHint` CSS selectors + readability fallback; robots.txt policy (`predelnewsbot` token, 24 h cache); Dapper repositories with MERGE upsert (no duplicate canonical URLs); `ScrapeJob` with per-source failure isolation, politeness delay and auto-disable sweep. Implementation conventions recorded in 05-integrations/scraping.md; source onboarding runbook + seed template added. **Awaits:** Q-1 confirmed source list (seed + per-source fixtures) and the M1 48-hour unattended run. |
