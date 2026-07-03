# 02 — Functional Specification

**Status:** Draft · **Last updated:** 2026-07-02

## The pipeline (happy path)

```
 ┌─────────┐   ┌─────────┐   ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌───────────┐
 │ 1 Scrape │──▶│ 2 Analyse│──▶│ 3 Detect │──▶│ 4 Draft  │──▶│ 5 Review │──▶│ 6 Publish │
 │  sources │   │  articles│   │  trends  │   │ + images │   │ Telegram │   │ FB + Umb. │
 └─────────┘   └─────────┘   └──────────┘   └──────────┘   └──────────┘   └───────────┘
```

### 1. Scrape
- Runs on a schedule (default: every 10 min; per-source override).
- Sources are configuration data (DB table), not code: name, RSS/sitemap/HTML URL, parser type,
  polling interval, enabled flag, politeness delay.
- Output: `SourceArticle` records — URL, title, extracted text, published-at, source, raw HTML hash.
- Deduplication by canonical URL + content hash; re-crawls update, never duplicate.

### 2. Analyse
- Each new `SourceArticle` gets: language check, category guess, region relevance score,
  named entities, one-paragraph summary (AI-assisted, cheap model, batched).
- Non-Bulgarian or clearly irrelevant items are marked `Ignored` (kept for audit).

### 3. Detect trends
- Clustering: group `SourceArticle`s covering the same story (AI-assisted grouping over a sliding
  window, default 48 h; deterministic pre-pass joins exact wire-copy duplicates by content hash
  without AI cost). One topic per article (v1).
- Trend score per topic (pure, testable code — no AI): weighted sum of article count (log),
  source diversity, velocity (articles in the last 6 h) and average region relevance, decayed by
  the age of the newest article (half-life 12 h). Tunables live in config under `Trend:*`
  (weights, `HotThreshold` default 6.0, `WindowHours` 48); tuning happens against real data
  (M2 backtest).
- A topic crossing `Trend:HotThreshold` becomes `Hot` (muted topics still collect articles and
  scores but are not promoted). Topics idle past the window become `Done`; no Hot demotion in v1.
- Manual trigger: the editor can send a Telegram command (`/draft <url or topic>`) to force a
  topic without waiting for the threshold.

### 4. Draft + images
- For each `Hot` topic not yet drafted, generate a `Draft`:
  headline, subtitle, body (original synthesis — never copied text), suggested category, region,
  tags, SEO title/description, and a source list (URLs used).
- Image suggestions (1–3) from allowed sources only (see 05-integrations/images.md), each with
  attribution metadata and a proposed alt text in Bulgarian.
- Every generation records: model, prompt version, token usage, cost.

### 5. Review in Telegram
- The bot posts to the editorial chat: headline, subtitle, body (or excerpt + full text as file if
  too long), chosen image with alternatives, category/tags, sources, cost. Inline keyboard:

| Button | Effect |
|---|---|
| ✅ Одобри (Approve) | Draft → `Approved`, publishing starts immediately |
| ✏️ Промени (Request changes) | Bot asks for instructions; editor replies in thread; regeneration produces a new draft version, re-posted for review |
| 🖼 Друга снимка (Другa image) | Cycle to next image suggestion / accept an image the editor uploads in reply |
| ❌ Откажи (Reject) | Draft → `Rejected` (with optional reason), topic muted for N hours |

- Unactioned drafts expire after a configurable TTL (default 12 h) → `Expired` (news goes stale).
- Every action is recorded with the Telegram user id and timestamp (audit trail).

### 6. Publish
- On `Approved`: publish to **Umbraco** first (create media item + content node under `newsRoot`,
  publish). On success, publish to **Facebook Page** (link post with the Umbraco URL, or photo +
  text — configurable). Partial failure → status `PartiallyPublished` + Telegram alert with a
  retry button. Success → confirmation message with live links.

## Draft lifecycle (state machine)

```
                    ┌───────────┐
   Topic(Hot) ────▶ │ Generating │──failure──▶ GenerationFailed (alert + retry)
                    └─────┬─────┘
                          ▼
                   PendingReview ──TTL──▶ Expired
                     │   │   │
        ✏️ changes ──┘   │   └── ❌ ──▶ Rejected
        (new version,    │
         back to         ✅
         PendingReview)  ▼
                      Approved
                          │
                          ▼
                     Publishing ──▶ Published
                          │
                          └──partial/failed──▶ PartiallyPublished / PublishFailed
                                                (alert + manual retry via Telegram)
```

Rules:
- Only `PendingReview` drafts accept editor actions.
- A regenerated draft is a **new version** linked to the same topic; old versions are kept —
  the replaced version gets status `Superseded` (v1 addition to the state machine), the new one
  starts at `Generating` with the editor's instructions and returns to `PendingReview`.
- `Approved` → `Publishing` is automatic and immediate; there is no scheduling in v1
  (scheduled publishing is a candidate for a later phase).

## Editor commands (Telegram)

| Command | Action |
|---|---|
| `/status` | Pipeline health: last scrape per source, queue sizes, today's cost |
| `/draft <url>` | Force-draft an article about the given source URL |
| `/topics` | List current hot topics and their draft states |
| `/mute <topic-id> [hours]` | Suppress a topic |
| `/pause` and `/resume` | Pause/resume automatic draft generation (scraping continues) |

## Configuration surface (managed without redeploys)

Sources list, polling intervals, trend threshold, sliding-window size, draft TTL, Telegram chat id,
category/region mapping, FB post format, AI provider + model per pipeline stage, daily request
and cost budgets.
Stored in the database (`Config` + `Source` tables); secrets are *not* configuration (see 06).
