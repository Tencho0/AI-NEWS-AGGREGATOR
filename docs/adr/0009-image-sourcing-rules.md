# ADR-0009 — Image sourcing rules (no scraped images)

**Status:** Proposed · **Date:** 2026-07-02

## Context

Every article needs a cover image (`coverImage` is mandatory on the site's `article` type).
The scraped source articles contain images, but press photos are licensed to their publishers —
republishing them is a copyright violation with concrete financial risk for a small outlet.

## Options considered

1. **Priority chain of legal sources**: own media library → free stock APIs (Pexels/Pixabay)
   → AI-generated illustration (abstract topics only, clearly labelled) → editor upload;
   attribution + licence recorded per image; human picks the final image in Telegram.
2. Reuse source images with credit — credit does not equal a licence. Rejected.
3. Always AI-generate — fastest, but photo-realistic generations of real events/people are an
   ethics/credibility hazard for a news outlet, and quality for local topics is poor.

## Decision

Option 1, with hard rules: never scraped images; identifiable real people only via own-library or
editor-supplied photos; AI images only as clearly-labelled illustrations for abstract topics.
Details in `docs/05-integrations/images.md`.

## Consequences

Slightly weaker visuals than "steal the wire photo", fully defensible legally; the own-library
tier improves over time as the site's media collection grows and gets tagged; the AI-generation
provider choice is deferred (Q-4) since tiers 1–2 + upload cover launch.
