# 10 — Roadmap: Phases, Milestones, Task Breakdown

**Status:** Draft · **Last updated:** 2026-07-02

Durations are effort estimates for one developer part-time; adjust after Phase 1 (velocity data).
Each phase ends with: demo of the milestone criterion, docs updated, progress log entry below.
**Detailed per-phase task plans are written at the start of each phase** (documentation-first) —
the breakdowns here are the seed, not the final word.

## Phase 0 — Foundations *(~2–3 days)*

Goal: repo + docs + walking skeleton.
- [x] Documentation tree (this) — review and confirm ADRs 0002–0009 with the owner
- [ ] Resolve blocking open questions: Q-1 sources, Q-2 style guide, Q-3 budget cap
- [ ] Solution scaffold (`Newsroom.Core/Infrastructure/Worker` + test projects), CI build
- [ ] `Newsroom` DB + migration runner + first migration (`nw_Source`, `nw_Config`, `nw_Log`)
- [ ] Serilog wiring; Windows Service host runs and heartbeats
- **Milestone M0:** empty worker runs as a service on dev, logs, applies migrations.

## Phase 1 — Scraping & storage *(~1 week)*

- [ ] Source model + admin seed for the confirmed source list
- [ ] RSS adapter; HTML adapter (AngleSharp + readability heuristic); canonical URL + hash dedup
- [ ] Politeness layer (robots.txt, conditional GET, per-host delay), Polly policies
- [ ] `ScrapeJob` on schedule; per-source failure isolation + auto-disable
- [ ] Fixture-based extraction tests for every launch source
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
