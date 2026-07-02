# 01 — Vision, Goals, Scope

**Status:** Draft (awaiting owner confirmation) · **Last updated:** 2026-07-02

## Vision

An AI-powered newsroom assistant for **Predel News** (Bulgarian regional news, Blagoevgrad /
Southwest Bulgaria) that watches selected news sources around the clock, detects trending and
emerging topics, drafts original Bulgarian-language articles with suggested images, and routes
every draft through **human editorial approval in Telegram** before publishing automatically to
the Facebook page and the existing Umbraco website.

The human editor stays in charge of *what* gets published; the system removes the manual labour
of *finding, drafting and distributing*.

## Goals

1. **Speed** — cut time from "topic emerges" to "article published" from hours to minutes
   (excluding human review time).
2. **Coverage** — never miss a hot regional/national topic during unattended hours.
3. **Quality & originality** — drafts are original syntheses (never copies) in correct Bulgarian,
   matching the site's editorial style.
4. **Editorial control** — nothing is published without explicit human approval; every published
   article is traceable to its sources, its draft versions, and its approver.
5. **Low operating cost** — runs on the existing Windows VPS; AI spend measured and bounded.

## Success metrics (initial targets — revisit after Phase 4)

| Metric | Target |
|---|---|
| Time from trend detection → draft in Telegram | < 15 min |
| Time from approval → live on Umbraco + Facebook | < 2 min |
| Drafts approved without edit requests | > 50 % after tuning |
| Pipeline uptime | > 99 % (excl. VPS maintenance) |
| AI cost per published article | tracked from day 1; budget cap configurable |

## Scope (in)

- Scraping/monitoring a configurable list of Bulgarian news sources (RSS-first, HTML fallback).
- Article analysis: dedup, categorisation, topic clustering, trend scoring.
- AI draft generation (headline, subtitle, body, tags, category, region, SEO fields) in Bulgarian.
- Image *suggestion* from legal sources (own media library, licensed stock, AI-generated).
- Telegram bot: draft delivery, approve / request-changes / reject / regenerate flow.
- Automated publishing to the Umbraco site (as the `article` document type) and the Facebook Page.
- Persistence, logging, monitoring, cost tracking, retry/error handling.

## Non-scope (explicitly out, at least for v1)

- **Automated posting to Facebook Groups** — the Groups API was deprecated by Meta (2024); no
  compliant automated path exists. See ADR-0008 and risk R-1. The existing list of ~28 target
  groups (in the Predel-News repo, `articles/групиЗаПостжане.txt`) can only be served manually
  or via a link the editor shares.
- Publishing scraped text or scraped images verbatim (legal / copyright — see 06-security.md).
- Multi-language content (site is invariant Bulgarian).
- Video/audio content generation.
- Comment moderation, newsletters, social listening beyond news sources.
- A web UI for review (Telegram is the review surface for v1).
- Automated publishing without human approval ("full-auto mode") — may be revisited later for
  low-risk categories, but requires its own ADR.

## Stakeholders

| Role | Who | Responsibility |
|---|---|---|
| Product owner / editor | Predel News editorial | Approves drafts, sets source list & style guide |
| Developer / operator | Tencho Bostandzhiev | Builds, deploys, operates |
| Systems it touches | Predel-News Umbraco site, Facebook Page, Telegram | Integration targets |

## Open questions (tracked in 11-risks-and-open-questions.md)

- Q-1: Final list of sources to scrape (national + regional).
- Q-2: Editorial style guide text (used verbatim in the AI system prompt).
- Q-3: Monthly AI budget ceiling.
