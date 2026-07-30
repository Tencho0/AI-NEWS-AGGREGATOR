# ADR-0011 — Cloudflare Workers AI (FLUX.1 Schnell) for cover-image generation

**Status:** Accepted · **Date:** 2026-07-30

## Context

ADR-0009 deferred the AI image-generation provider choice (Q-4) because stock + editor upload
covered launch. In practice every article needs a cover, and free stock results for local
Bulgarian topics (a Blagoevgrad municipal session, a Pirin road closure) are generic filler —
the same handful of unrelated stock photos over and over. An on-topic illustration per article
would be a clear quality win, but only at zero cost (the project runs on free tiers, ADR-0010).

## Options considered

1. **Stay stock-only** — no new dependency, but covers stay generic; the tier-1 own-library
   is not implemented, so tier 2 carries everything.
2. **Paid hosted image APIs** (DALL·E, Stability, Gemini image generation) — per-image cost
   conflicts with the $0 AI budget; quality overkill for illustration-style covers.
3. **Cloudflare Workers AI, FLUX.1 Schnell** (`@cf/black-forest-labs/flux-1-schnell`) — plain
   REST endpoint, free tier (10,000 neurons/day) comfortably covers a few articles per day,
   fast (schnell) distilled model suited to short-step generation. Chosen.

## Decision

Option 3, **generation-first with automatic stock fallback**: when a draft is generated,
`FeaturedImageService` tries FLUX generation before the draft reaches Telegram review; on *any*
failure (unconfigured, daily budget exhausted, HTTP/429, malformed payload) it falls back to the
existing Pexels/Pixabay suggestions — the pipeline never stalls on the new dependency.

- Prompt (`FluxPromptComposer`) is built from the article's own details only — the drafting
  model's English `imageSearchQueries` plus headline — with hard style directives keeping
  ADR-0009's tier-3 rules: clearly an illustration (never photo-realistic), no real
  identifiable people, no embedded text/letters/logos. Max 2,048 chars (FLUX limit).
- Default 1280×720 — above the site's 1,200 px cover warning (Google Discover large-image
  minimum). Steps default 4 (max 8).
- Image saved on the worker's disk (`Images:Cloudflare:GeneratedImageDir`), stored as
  `nw_DraftImage` with `SourceKind='ai'` (already reserved in the schema), attribution
  "Илюстрация", the draft's AI-written Bulgarian alt text.
- Daily request cap via `Ai:Stages:Image:DailyRequestBudget`, metered through the existing
  `IAiBudget`/`nw_CostLedger` (stage "Image", cost 0 on free tier; visible in `/quota`).
- Config under `Images:Cloudflare:*` (AccountId, ApiToken, Model, Steps, Width, Height,
  GeneratedImageDir); secrets per 06-security.md, never in git.

Details in `docs/05-integrations/images.md`.

## Consequences

Unique, on-topic covers at $0 instead of generic stock; covers meet the Discover size minimum.
New external dependency with a free-tier quota — the budget cap plus automatic stock fallback
keeps drafting running when it's exhausted or down. Generated images live on the worker's disk
(sent to Umbraco as base64, to Facebook as bytes), so worker disk joins the things to watch.
In Telegram, a successful generation is the single suggestion (no stock cycling); the editor
can still reject, regenerate, or attach their own photo. Q-4 is resolved.
