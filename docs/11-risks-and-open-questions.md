# 11 — Risks & Open Questions

**Status:** Living document · **Last updated:** 2026-07-02

## Risk register

| Id | Risk | Likelihood | Impact | Mitigation / plan |
|---|---|---|---|---|
| R-1 | **Facebook Groups can't be automated** (Groups API deprecated 2024) — a key distribution channel (28 groups) stays manual | Certain | Medium | Accepted (ADR-0008). Reduce friction: confirmation message contains ready-to-paste teaser + link. Revisit if Meta ships an API. |
| R-2 | **Meta app review / business verification delays or rejection** blocks Page publishing | Medium | High for Phase 6 | Start provisioning in Phase 0; fallback: manual FB posting from the confirmation message until approved. |
| R-3 | **Source ToS / legal pushback on scraping** | Low–Medium | Medium | RSS-first (feeds are an invitation), politeness rules, per-source ToS check at onboarding, takedown = disable source (config, minutes). |
| R-4 | **Copyright violation via images or too-close paraphrase** | Medium if careless | High (fines, reputation) | Hard rules: no scraped images (ADR-0009), originality instructions + self-check + human gate; editor training on the rubric. |
| R-5 | **AI hallucination published** (fabricated fact/quote survives review) | Medium | High (credibility) | Sources listed in review message; self-check flags unverifiable claims; style guide mandates attribution; editor accountability recorded. |
| R-6 | **AI cost overrun** (only relevant once any stage moves to a paid tier/provider) | Low (free tier) → Medium (paid) | Medium | `nw_CostLedger` meters cost from day 1 even at $0; daily cost budget config; per-stage token limits; cost shown in every review message. |
| R-7 | **Trend detection tuned wrong** (spam drafts or missed stories) | High initially | Medium | Phase-2 backtest on real corpus; threshold + mute controls; `/draft` manual override covers misses; expect a tuning period. |
| R-8 | **Single VPS is a single point of failure** (site + worker + DB) | Low | Medium | Accepted for v1 (matches site's posture). Service auto-restart; DB in existing backup regime — **confirm `Newsroom` db added to backups (Q-5)**. |
| R-9 | **Telegram as the only control surface** — editor unavailable ⇒ nothing publishes | Medium | Low–Medium | By design (human gate). Multiple editors on allowlist; TTL expiry keeps queue clean. |
| R-10 | **Umbraco upgrades break the publishing endpoint** | Low | Medium | Endpoint lives in the site's repo and compiles with it → breaks visibly at build time, not silently; contract tests on both sides. |
| R-11 | **Gemini free-tier quota exhaustion or limit changes** (~15 RPM / ~1,500 RPD, terms have shifted before — Pro removed from free tier 2026-04) | Medium–High | Medium | Client-side throttle + per-stage daily request budgets + request packing; queue-don't-drop on 429; alert at 80 %; upgrade paths in ADR-0010 are config changes. |
| R-12 | **Flash-tier model quality insufficient for Bulgarian long-form drafting** | Medium | High for the product's core value | Phase-3 golden-set eval is the explicit gate; provider abstraction (ADR-0010) makes a drafting-stage upgrade (Gemini paid / other provider) a config + budget decision, not a rewrite. |

## Open questions (each resolution → ADR or decision-log entry)

| Id | Question | Needed by | Owner |
|---|---|---|---|
| Q-1 | ✅ **Resolved 2026-07-03** — owner named 8 sources; 6 have verified feeds and are live (БТА, Mediapool, Струма, Топ Преса, ИнфоМрежа, Благоевград24 — see `tools/seed-sources.sql`); 2 deferred pending sitemap/html adapters (pirinsko.com — no RSS; blagoevgrad.bg — sitemap only). Feeds probed with the bot UA; runtime robots.txt compliance enforced by the scraper | Done | — |
| Q-2 | Editorial style guide — **draft written** ([editorial-style-guide.md](editorial-style-guide.md), derived from the two sample articles); awaiting owner review + answers to its 4 open points. Used verbatim in the drafting prompt from Phase 3 on | Review any time (draft unblocks Phase 3) | Editor |
| Q-3 | AI budget policy: launch is free-tier ($0); what monthly ceiling applies **if/when** a stage upgrades to paid (triggers: R-11 quota pressure or R-12 quality gate) | Phase 3 (eval time) | Owner |
| Q-4 | AI image generation provider (if tier-3 images are wanted) | Phase 3+ | Dev |
| Q-5 | Is the SQL Express backup regime covering new databases automatically? | Phase 0 | Dev |
| Q-6 | Author identity for automated articles on the site (dedicated "Predel News" staff author vs. per-editor) | Phase 5 | Editor |
| Q-7 | FB post format preference (link post vs photo post) per category | Phase 6 | Editor |
| Q-8 | Which Telegram accounts are on the approver allowlist? | Phase 4 | Owner |
| Q-9 | ~~BgGPT (INSAIT) organizational API access~~ **Parked** (owner 2026-07-02: Gemini only). Reopen only if the Phase-3 golden-set eval fails on Gemini Flash (R-12) — then BgGPT is the first alternative to investigate (research/2026-07-free-ai-providers.md) | — | — |

## Assumptions (made in this plan; flag if wrong)

- A-1: The aggregator may reuse the VPS and SQL Express instance (capacity is sufficient).
- A-2: Content language is Bulgarian only, matching the site.
- A-3: The site's existing `article` document type is the publishing target as-is (no new doc type).
- A-4: One editorial Telegram chat is an acceptable review surface for v1 (no web UI).
- A-5: Publishing volume stays in "a few articles per day" territory (drives all rate/cost sizing).
