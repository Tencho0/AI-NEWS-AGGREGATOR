# Integration — Image Sourcing & Suggestion

**Status:** Draft · **Last updated:** 2026-07-02 · **ADR:** 0009

## Hard rule

**Never reuse images from scraped articles.** Press photos are licensed to their publishers;
republishing them is a copyright violation with real financial risk (see risk R-4, 06-security.md).

## Sourcing priority (ADR-0009)

| # | Source | When | Attribution |
|---|---|---|---|
| 1 | **Own media library** (existing site media, tagged by topic: town views, institutions, recurring subjects) | Recurring local topics — best authenticity | none needed |
| 2 | **Free stock APIs** — Pexels, Pixabay (free licences, API access) | Generic/illustrative needs | per licence; stored in `nw_DraftImage.Attribution` and shown in caption/credit |
| 3 | **AI-generated illustration** (only for abstract topics — economy, weather, statistics; clearly styled as illustration, never photo-realistic depictions of real events/people) | When 1–2 yield nothing | caption "Илюстрация" |
| 4 | **Editor upload** via Telegram reply | Editor has a real photo (own/press-release material) | editor's responsibility |

The drafting model outputs 2–3 English `imageSearchQueries`; the image service queries sources in
priority order and returns up to 3 candidates with attribution + AI-written Bulgarian alt text
(the site validates cover-image alt text).

## Rules

- Real, identifiable people: only own-library or editor-supplied images.
- Every image stored with: source kind, origin URL/id, licence string, attribution, alt text.
- The selected image is uploaded to Umbraco as a Media item by the publishing endpoint;
  Facebook uses the article's OG image (no separate re-hosting in v1).
- AI image generation provider: **deferred decision** (open question Q-4) — not required for MVP;
  tiers 1–2 + editor upload cover launch.
