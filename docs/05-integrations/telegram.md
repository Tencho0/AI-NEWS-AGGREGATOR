# Integration — Telegram Review Bot

**Status:** Draft · **Last updated:** 2026-07-13 · **ADR:** 0006

## Approach

`Telegram.Bot` NuGet, **long polling** (`getUpdates`) from the worker — no inbound webhook, no
public endpoint, no certificate wiring on the VPS (ADR-0006). Poll offset persisted in
`nw_TelegramState` so restarts never lose updates.

## Chats

| Chat | Purpose |
|---|---|
| Editorial group (or the editor's private chat) | Draft review flow |
| Admin thread/chat (may be the same, different topic) | Alerts: failures, cost cap, source health |

Chat ids are configuration; the bot **ignores every update from unknown chats/users**
(allowlist of Telegram user ids = the editors; this is the authorization model).

## Review message format

One message per draft version:

```
🔥 <topic label> (score 8.2, 5 източника)
━━━━━━━━━━━━━━━
<Headline — bold>
<Subtitle — italic>

<body — first ~1500 chars; full text attached as .md file if longer>

📎 Категория: <…> · Регион: <…> · Тагове: <…>
🔗 Източници: <numbered links>
⚠️ За проверка: <flagged claims, if any>
💰 <cost> · v<version> · модел <model>
```
+ photo message with the top image suggestion (attribution in caption)
+ inline keyboard: ✅ Одобри · ✏️ Промени · 🖼 Друга снимка · ❌ Откажи

## Interaction rules

- **Callback handling is idempotent** — double-taps and stale buttons (draft no longer
  `PendingReview`) answer with a toast ("вече обработено") and do nothing.
- ✏️ Промени: bot replies "Опиши промените…", stores a pending-conversation row keyed by
  draft id; the editor's next reply in that thread becomes the regeneration instruction.
  New version posted as a fresh message; old message's keyboard is removed.
- 🖼: edits the photo message to the next suggestion; an editor photo-upload reply overrides
  suggestions (stored as `editor-upload`, wins selection).
- After approve/reject, the message is edited to show final state + (on publish) live links.
- All state transitions via the bot are recorded in `nw_ReviewAction` (user id, action, time).

## Commands

All commands are **allowlist-gated** (same authorization model as the review flow): only
editor user ids in the review chat are honoured; every other update is silently ignored.
Slash-command routing lives in [`ReviewUpdateRouter.RouteText`](../../src/Newsroom.Core/Review/ReviewUpdateRouter.cs);
each command's response is in [`TelegramJob`](../../src/Newsroom.Worker/Jobs/TelegramJob.cs).

### Slash commands (typed in the review chat)

| Command | Effect |
|---|---|
| `/status` | Posts a status summary of the worker (queue/draft state). |
| `/topics` | Lists the top tracked topics. |
| `/quota` | AI requests used vs the daily per-stage cap today. |
| `/health` | Last heartbeat per background job, with a staleness marker. |
| `/help` | Prints the command list. |
| `/mute <topicId> [hours]` | Silences one topic. `hours` is optional and **defaults to 24**; e.g. `/mute 42` or `/mute 42 6`. Replies with confirmation or "няма такава тема" if the id is unknown. |
| `/unmute <topicId>` | Lifts a topic mute early (reverse of `/mute`). |
| `/pause` | Stops **draft generation** (runtime flag `Draft:Paused` in `nw_Config`). Scraping and analysis keep running. |
| `/resume` | Clears the pause flag — draft generation resumes on the next DraftJob cycle. |
| `/draft <topicId>` | Force-draft a topic even if it is not Hot. |

In group chats the `@BotName` suffix (`/status@MyBot`) is accepted and stripped.

### Card actions (per draft, not typed)

These act on a specific draft the bot posted; they arrive as inline-keyboard callbacks or as
replies to the review card:

| Action | Trigger | Effect |
|---|---|---|
| Approve | ✅ button | Publishes the draft. |
| Reject | ❌ button | Discards the draft. |
| Request changes | ✏️ button, then a text reply | Opens a pending conversation; the next message becomes regeneration instructions. |
| Cycle image | 🖼 button | Shows the next stock image suggestion. |
| Submit instructions | Text reply to a review card | Binds the words to that card's draft as change instructions (unambiguous even with several drafts waiting). |
| Attach photo | Photo reply to a review card | Stores the upload as `editor-upload`; it wins image selection. |

There is **no `/start`** — unrecognised text is silently ignored. `/draft <url>`
is a planned Phase 4b command and is **not routed yet**.

## Implementation notes (v1 — Phase 4a)

- Messages use Telegram **HTML parse mode** (all interpolated content escaped); body preview
  truncated at ~1500 chars on a word boundary. Full-text attachment, 🖼 image cycling,
  editor photo upload and `/draft <url>` arrive in **Phase 4b**.
- Change requests: original draft → `Superseded`; new `Generating` row carries
  `RegenInstructions` + `ParentDraftId`; DraftJob regenerates with the editor's instructions +
  the previous body as context; the new version is re-dispatched to the chat.
- `/pause` sets a **runtime flag in the database** (`nw_Config` `Draft:Paused`) read by DraftJob
  each cycle — no restart needed; scraping/analysis continue while paused.
- Poll offset persists in `nw_Config` (`Telegram:UpdateOffset`); pending change-conversations in
  `nw_TelegramPending` (unique per chat+user).
- Without `Telegram:BotToken`/`ReviewChatId`/`AllowedUserIds` configured, the job logs one
  warning and stays dormant (mirrors the AI stages' key-less degradation).

## Failure behaviour

- Telegram API down → jobs continue up to `PendingReview`; delivery retried with backoff;
  nothing is lost (drafts sit in DB).
- If a draft can't be delivered within its TTL, it expires like an unreviewed draft.
