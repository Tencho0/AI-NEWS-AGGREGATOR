# Integration — Facebook Publishing

**Status:** Draft · **Last updated:** 2026-07-17 · **ADR:** 0008

## What is possible (and what is not)

| Target | Status |
|---|---|
| **Facebook Page** (the Predel News page) | ✅ Supported via Graph API (`POST /{page-id}/feed` or `/photos`) with a Page access token |
| **Facebook Groups** (the ~28 regional groups listed in Predel-News `articles/групиЗаПостжане.txt`) | ❌ **Not automatable.** Meta deprecated the Groups API (April 2024). No compliant programmatic posting to groups exists. Automating a user account (browser automation) violates Meta ToS and risks account/page bans — explicitly rejected. |

Group distribution therefore stays **manual**: the publish-confirmation Telegram message includes
the article URL + a ready-to-paste teaser text, so the editor can share into groups in seconds.
(Recorded as ADR-0008; revisit only if Meta ships a new API.)

## Page publishing flow

Which of two independent shapes a draft gets is decided per draft by `nw_Draft.PublishTarget`
(docs/superpowers/specs/2026-08-06-per-draft-publish-target-design.md), not by a process-wide
switch — both queries run every cycle:

- **Link post** (`Both`-target drafts, approved ✅ — `IPublishRepository.GetPendingFacebookAsync`)
  — runs **after** a successful Umbraco publish (needs the live URL). Post format (configurable
  per category):
  - default: message = headline + 1–2 sentence teaser (AI-generated with the draft) + link → FB
    renders the OG card from the Umbraco page (`ogImage`/SEO composition already exists on the
    site);
  - alternative: **photo post** with text + link in message (better reach, needs image rights
    also for FB — only images we may re-host).
  - Failure → the draft stays `PartiallyPublished` (site is live, FB is not) + Telegram alert
    telling the editor the site is up and to post the page manually.
- **Standalone post** (`Facebook`-target drafts, approved 📘 Само ФБ —
  `IPublishRepository.GetApprovedForFacebookAsync`) — posts straight from `Approved`, no site
  step, no link; see "Post composition" below. Failure → nothing is published anywhere (the
  draft demotes `Approved → PublishFailed`) + a Telegram alert that says so explicitly.

Either way: store the returned post id + permalink in `nw_PublishRecord`.

## Post composition (standalone Facebook posts)

Drafts approved 📘 Само ФБ post straight to the page with no site publish and no link, in every
mode — this is not something `Publishing:FacebookOnly` turns on (see "Interaction with
Publishing:FacebookOnly" below for what the flag actually does). The composed message follows a
strict priority per draft:

1. **Editor `/post` drafts** (`PromptVersion = editor-v1`) — body published verbatim (unchanged).
2. **Caption drafts** (`nw_Draft.FacebookCaption` set; `draft-v2`+) — the post is
   `FacebookCaption.Compose(caption, hashtags)`: the social caption (sentence-case hook line,
   1–2 short paragraphs, closing question/CTA), a blank line, then 2–3 hashtags. Posted
   **verbatim** — no markdown stripping, no headline (the hook opens the post).
3. **Legacy drafts** (`FacebookCaption` NULL — prompts older than `draft-v2`) — the original
   format: ALL-CAPS headline + full stripped body (`FacebookTeaser.ComposeFullBody`). This was
   the *only* format before the 2026-07-17 engagement round; it now only drains the in-flight
   backlog of older drafts.

`GetPendingFacebookAsync` applies the same priority to the link post for `Both` drafts once their
site publish has succeeded: a caption-carrying draft posts the caption + hashtags with no headline
(still with the `link`); a caption-less draft keeps the short teaser + headline described under
"Page publishing flow" above.

### Interaction with `Publishing:FacebookOnly`

`Publishing:FacebookOnly` survives as an ops kill-switch (temporary, decision-log 2026-07-08;
`runbooks/start-the-worker.md` §5), and it **overrides the column for `Both` drafts only**: while
it is on, the Umbraco leg is skipped entirely and the standalone leg above additionally accepts
`Both` drafts, so a draft approved ✅ (or scheduled 📅, which always writes `Both`) still reaches
the page as a standalone post instead of stalling forever on a site publish the flag has disabled.
`Website`-target drafts (🌐 Само сайт) are **not** widened in — that button means the editor
explicitly does not want a Facebook post, and the flag does not override it; a `Website` draft
approved while the flag is on simply waits, still `Approved`, until the flag clears.

## Scheduling (`Facebook:Schedule`)

- The review card's second keyboard row, 📅 **Насрочи HH:mm**, approves the draft gated on
  `nw_Draft.ScheduledForUtc`; the Facebook leg's publish queries (`GetApprovedForFacebookAsync`
  for the standalone leg and `GetPendingFacebookAsync` for the link leg) both skip a scheduled
  draft until the slot passes (`ScheduledForUtc <= SYSUTCDATETIME()`).
- The suggested slot (`PublishSlotSuggester`, recomputed at press time — the label on the button
  is only advisory) is the earliest local time ≥ now + `LeadMinutes` inside a `Windows` range,
  ≥ `MinGapMinutes` from every published or scheduled post, on a day with < `MaxPerDay` posts.
  Defaults: `07:30-09:30` / `12:00-13:30` / `17:30-21:30`, 90 min gap, 5/day, 5 min lead —
  heuristic v1; data-driven windows arrive with smart insights (10-roadmap.md backlog).
- ✅ **Одобри** stays immediate on a pending draft; pressed on an already-scheduled draft it
  clears the schedule instead of publishing twice (`nw_ReviewAction.Action = 'ScheduleOverridden'`),
  and the draft then publishes on the next ordinary cycle.
- If the slot computation fails when 📅 is pressed (e.g. a bad `Facebook:Schedule` config), the
  press is a no-op and the editor gets a "Грешка — опитай пак" toast — nothing is scheduled.

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
- **Status semantics (`Both`-target drafts):** Facebook not configured → `Published` on site
  success alone (site-only operation stays first-class). Facebook configured → site success moves
  the draft to `PartiallyPublished` until the page post succeeds (then `Published`); the site
  being live is never blocked or rolled back by Facebook failures. Transient Graph errors retry
  (`Facebook:MaxAttempts`, default 3); OAuth/permission errors are terminal + alert. A
  `Website`-target draft reaches `Published` on the site publish alone, regardless of Facebook
  configuration; a `Facebook`-target draft reaches `Published` on the page post alone, with no
  site step at all (`PublishTargets.RequiredDestinations`).
- **Teaser** = the draft's SEO description (fallback: first ~200 chars of the body as plain
  text). Post = message "{headline}\n\n{teaser}" + `link` (OG card rendered by FB) — unless the
  draft carries a Facebook caption (see "Post composition" above), in which case the caption +
  hashtags replace the teaser and the headline is dropped from the message.
- **Token health:** daily `GET /{page-id}` probe when configured; failure → ⚠️ Telegram alert
  pointing at the re-auth runbook. Graph error code 190 during posting raises the same alert.
- **Group-share helper:** every site-publish confirmation in Telegram includes a copy-paste
  `<pre>` block (headline + teaser + URL) for manual sharing into the ~28 regional groups.

## Rate/behaviour limits

Page posting volume is low (a few posts/day) — far under Graph API limits. Still: single retry
policy with backoff, and never post the same draft twice (idempotency by `nw_PublishRecord`).
