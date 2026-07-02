# Integration — Facebook Publishing

**Status:** Draft · **Last updated:** 2026-07-02 · **ADR:** 0008

## What is possible (and what is not)

| Target | Status |
|---|---|
| **Facebook Page** (the Predel News page) | ✅ Supported via Graph API (`POST /{page-id}/feed` or `/photos`) with a Page access token |
| **Facebook Groups** (the ~28 regional groups listed in Predel-News `articles/групиЗаПостжане.txt`) | ❌ **Not automatable.** Meta deprecated the Groups API (April 2024). No compliant programmatic posting to groups exists. Automating a user account (browser automation) violates Meta ToS and risks account/page bans — explicitly rejected. |

Group distribution therefore stays **manual**: the publish-confirmation Telegram message includes
the article URL + a ready-to-paste teaser text, so the editor can share into groups in seconds.
(Recorded as ADR-0008; revisit only if Meta ships a new API.)

## Page publishing flow

1. Runs **after** successful Umbraco publish (needs the live URL).
2. Post format (configurable per category):
   - default: **link post** — message = headline + 1–2 sentence teaser (AI-generated with the
     draft) + link → FB renders the OG card from the Umbraco page (`ogImage`/SEO composition
     already exists on the site);
   - alternative: **photo post** with text + link in message (better reach, needs image rights
     also for FB — only images we may re-host).
3. Store returned post id + permalink in `nw_PublishRecord`.
4. Failure → `PartiallyPublished` (site is live, FB is not) + Telegram alert with retry button.

## Access & tokens (start early — app review takes weeks)

- Meta developer app (Business type) linked to the page's Business Manager.
- Permissions: `pages_manage_posts`, `pages_read_engagement` → **App Review** + business
  verification required for live mode.
- Use a **long-lived Page access token**; expiry/invalidations detected by a daily token
  health-check job → Telegram alert with re-auth instructions (runbook in 07-operations.md).
- Token stored as a secret (06-security.md), never in DB or logs.

## Rate/behaviour limits

Page posting volume is low (a few posts/day) — far under Graph API limits. Still: single retry
policy with backoff, and never post the same draft twice (idempotency by `nw_PublishRecord`).
