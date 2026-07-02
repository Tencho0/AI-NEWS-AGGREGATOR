# ADR-0008 — Facebook: Page publishing only; no group automation

**Status:** Proposed · **Date:** 2026-07-02

## Context

Distribution targets include the Predel News Facebook Page and ~28 regional Facebook groups
(list exists in the Predel-News repo, `articles/групиЗаПостжане.txt`). Meta deprecated the
Groups API in April 2024 — there is no compliant programmatic way to post to groups. Page
publishing via the Graph API remains fully supported (`pages_manage_posts`).

## Options considered

1. **Automate Page posts only; make manual group sharing frictionless** — the publish
   confirmation in Telegram carries the live URL + a ready-to-paste teaser, so a human can share
   to groups in seconds.
2. Browser/user-account automation for groups — violates Meta ToS; realistic risk of losing the
   user account *and* the Page. Rejected.
3. Skip Facebook entirely for v1 — loses the main reach channel; unnecessary since Page API works.

## Decision

Option 1. Page posting is automated after successful site publishing; group distribution is a
documented manual step assisted by the bot.

## Consequences

The group channel keeps a human in the loop (slower, but safe); the Meta app review /
business verification process for `pages_manage_posts` must start early (risk R-2). Revisit only
if Meta ships a new compliant surface for groups.
