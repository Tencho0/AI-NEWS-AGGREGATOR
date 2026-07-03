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

## Implementation notes (v1 — Phase 6)

- **Dormant without credentials** (`Facebook:PageId` + `Facebook:AccessToken`), like every other
  stage; **DryRun defaults to ON** — with credentials + DryRun the exact message/link is logged
  and the flow completes as a success marked "(пробен режим)", which is the staging mode from
  09-deployment.md.
- **Status semantics:** Facebook not configured → `Published` on site success alone (site-only
  operation stays first-class). Facebook configured → site success moves the draft to
  `PartiallyPublished` until the page post succeeds (then `Published`); the site being live is
  never blocked or rolled back by Facebook failures. Transient Graph errors retry
  (`Facebook:MaxAttempts`, default 3); OAuth/permission errors are terminal + alert.
- **Teaser** = the draft's SEO description (fallback: first ~200 chars of the body as plain
  text). Post = message "{headline}\n\n{teaser}" + `link` (OG card rendered by FB).
- **Token health:** daily `GET /{page-id}` probe when configured; failure → ⚠️ Telegram alert
  pointing at the re-auth runbook. Graph error code 190 during posting raises the same alert.
- **Group-share helper:** every site-publish confirmation in Telegram includes a copy-paste
  `<pre>` block (headline + teaser + URL) for manual sharing into the ~28 regional groups.

## Rate/behaviour limits

Page posting volume is low (a few posts/day) — far under Graph API limits. Still: single retry
policy with backoff, and never post the same draft twice (idempotency by `nw_PublishRecord`).
