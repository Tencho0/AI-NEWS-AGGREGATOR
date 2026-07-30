# Integration — Image Sourcing & Suggestion

**Status:** Draft · **Last updated:** 2026-07-30 · **ADR:** 0009, 0011

## Hard rule

**Never reuse images from scraped articles.** Press photos are licensed to their publishers;
republishing them is a copyright violation with real financial risk (see risk R-4, 06-security.md).

## Sourcing priority (ADR-0009, ADR-0011)

Implemented automated flow: for every automated draft, `FeaturedImageService` **generates an AI
illustration first** (ADR-0009's tier 3, implemented via Cloudflare Workers AI FLUX.1 Schnell —
ADR-0011); on any failure it falls back automatically to the free stock APIs.

| # | Source | When | Attribution |
|---|---|---|---|
| 1 | **AI-generated illustration** — Cloudflare Workers AI, FLUX.1 Schnell (ADR-0011); clearly styled as illustration, never photo-realistic; no real people, no embedded text/logos | Tried first for every automated draft | caption "Илюстрация" |
| 2 | **Free stock APIs** — Pexels, Pixabay (free licences, API access) | Automatic fallback when generation is unconfigured, over budget, or fails | per licence; stored in `nw_DraftImage.Attribution` and shown in caption/credit |
| 3 | **Own media library** (existing site media, tagged by topic: town views, institutions, recurring subjects) | Aspirational — ADR-0009's tier 1, **not implemented** | none needed |
| 4 | **Editor upload** via Telegram reply | Editor has a real photo (own/press-release material) | editor's responsibility |

The drafting model outputs 2–3 English `imageSearchQueries`; every candidate carries attribution
+ AI-written Bulgarian alt text (the site validates cover-image alt text). When AI generation
succeeds it is the single suggestion in the Telegram review card (no stock cycling); when it
fails the editor sees up to 3 stock candidates as before. The editor can always reject,
regenerate, or attach their own photo.

## Rules

- Real, identifiable people: only own-library or editor-supplied images.
- Every image stored with: source kind, origin URL/id, licence string, attribution, alt text.
- The selected image is uploaded to Umbraco as a Media item by the publishing endpoint;
  Facebook uses the article's OG image (no separate re-hosting in v1).
- AI image generation provider: **Q-4 resolved by ADR-0011** — Cloudflare Workers AI,
  FLUX.1 Schnell (`@cf/black-forest-labs/flux-1-schnell`, free tier). Prompt composed from the
  article's own details only (English `imageSearchQueries` + headline) with hard no-text /
  no-real-people / illustration-only directives; attribution "Илюстрация"; default 1280×720
  (≥ the site's 1,200 px Discover minimum); file saved on the worker's disk
  (`Images:Cloudflare:GeneratedImageDir`), row stored with `SourceKind='ai'`; metered under
  budget stage "Image" (`Ai:Stages:Image:DailyRequestBudget`, cost 0 on free tier); falls back
  to stock on any failure or quota exhaustion. Config: `Images:Cloudflare:{AccountId, ApiToken,
  Model, Steps, Width, Height, GeneratedImageDir}` — secrets per 06-security.md, never in git.
