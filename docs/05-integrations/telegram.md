# Integration — Telegram Review Bot

**Status:** Draft · **Last updated:** 2026-07-02 · **ADR:** 0006

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

`/status`, `/draft <url>`, `/topics`, `/mute`, `/pause`, `/resume` — defined in
[02-functional-spec.md](../02-functional-spec.md). Commands are also allowlist-gated.

## Failure behaviour

- Telegram API down → jobs continue up to `PendingReview`; delivery retried with backoff;
  nothing is lost (drafts sit in DB).
- If a draft can't be delivered within its TTL, it expires like an unreviewed draft.
