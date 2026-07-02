# ADR-0007 — Dedicated authenticated publishing endpoint inside the Umbraco site

**Status:** Proposed · **Date:** 2026-07-02

## Context

Approved drafts must become published `article` nodes on the Umbraco 17 site, including a Media
item for the cover image, correct slug (Cyrillic→Latin via the site's `ISlugGenerator`), taxonomy
picker values, and SEO fields. The site currently exposes no write API (Delivery API disabled;
custom controllers exist only behind backoffice auth). Verified against the codebase 2026-07-02.

## Options considered

1. **Custom `PublishingApiController` in `PredelNews.BackofficeExtensions`, auth via a dedicated
   Umbraco API user (client credentials)** — the site owns its invariants (slug, media formats,
   picker JSON, validation); the worker sends a simple semantic payload; follows the repo's
   established controller pattern.
2. Generic Umbraco Management API from outside with an API user — no site code, but the worker
   must replicate MediaPicker3 JSON, slug rules, taxonomy resolution → brittle coupling to
   Umbraco internals across upgrades.
3. Direct writes to the Umbraco database — rejected outright: bypasses cache, Examine index,
   notifications, and every invariant.
4. Drop drafts as unpublished content for backoffice approval — duplicates the Telegram gate and
   slows the workflow.

## Decision

Option 1. Contract (request/response, idempotency via `externalRef`) specified in
`docs/05-integrations/umbraco.md`; contract changes require an ADR here plus a matching
Predel-News PR; contract tests on both sides.

## Consequences

One small, well-owned addition to the site's codebase; upgrades break loudly at compile time
rather than silently over HTTP; the worker stays ignorant of Umbraco internals. Cross-repo
coordination is the cost — mitigated by the written contract and tests.
