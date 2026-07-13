# Design — Editor-authored articles via Telegram (`/post`, `/new`)

**Date:** 2026-07-13 · **Status:** Approved · **Related:** docs/05-integrations/telegram.md,
docs/02-functional-spec.md §4–5, spec 2026-07-13-telegram-editor-commands-design.md

## Goal

Let an editor create an article by sending text in the Telegram review chat:

| Command | Purpose | AI cost |
|---|---|---|
| `/post <text>` | Verbatim article: what you send is what publishes. First line = headline, rest = body. | none |
| `/new <text>` | AI-assisted: the text (notes, press release) is raw material; the AI writes a styled article. | 1 Draft (+1 SelfCheck) request |

Both flow into the existing review card → ✅ / ✏️ / ❌ → publish path (Facebook-only mode
today). Same authorization as every command: allowlisted editor ids in the review chat;
everything else silently ignored.

## Non-goals (YAGNI)

- `/draft <url>` stays Phase 4b — out of scope.
- Multi-message articles: one Telegram message = one article (4096-char cap fits FB posts).
- Photo-with-caption as the initial message (captions cap at 1024 chars). Photos attach the
  existing way: reply a photo to the review card (`editor-upload` wins selection — already live).
- Website (Umbraco) specifics: v1 targets the current Facebook-only mode. When site publishing
  turns on, manual drafts will need a category/SEO default — recorded as an open question in
  docs/11, not solved here.

## Architecture — reuse the force-draft seam

Verified against the code (all of today's `/draft <topicId>` work is implemented):

- `ReviewRepository.GetPendingDispatchAsync` posts a card for **any** `PendingReview` draft with
  `TelegramMessageId IS NULL` — a verbatim draft only has to be inserted in that state.
- `DraftRepository.GetTopicsNeedingDraftAsync` picks up any topic with `ForceDraftAtUtc` set,
  bypassing the Hot gate — a manual topic rides this unchanged.
- `DraftJob` builds its prompt from `GetTopicBundleAsync`; both the fresh-draft and ✏️-regen
  paths fail when the bundle has no articles. Synthesizing the bundle for manual topics at that
  single seam makes generation, validation, self-check, image suggestions **and** later
  regenerations work with no other DraftJob changes.

### Data model — migration `0012_manual_topics.sql`

```sql
ALTER TABLE dbo.nw_Topic ADD EditorInput NVARCHAR(MAX) NULL;
```

- New `TopicStatus.Manual` enum value (`nw_Topic.Status = 'Manual'`). Manual topics are created
  only by these commands; the trend job selects by Emerging/Hot and never touches them; they are
  excluded from `/topics` output.
- `EditorInput` = the editor's original text, kept on the **topic** so
  `GetTopicBundleAsync(topicId, …)` can synthesize the bundle for every draft version (v1 and
  each ✏️ regeneration) without new draft columns.
- `nw_Draft` is unchanged: `TopicId NOT NULL` keeps its FK (this is why manual topics exist —
  a nullable `TopicId` would ripple through every review/publish join).

### `/new <text>` — AI-assisted path

`IDraftRepository` gains `CreateManualTopicAsync(text)` (draft-side write, and `TelegramJob`
already injects this interface for force-draft), one transaction:

1. INSERT `nw_Topic`: `Status='Manual'`, `Label` = first ~60 chars of the text (word boundary),
   `Score=0`, `EditorInput=text`, `ForceDraftAtUtc=SYSUTCDATETIME()`.
2. Reply `✍️ Статията се пише — ще я получиш за преглед.`

DraftJob's next cycle picks it up via the existing forced-topic ordering (forced topics jump the
queue). `GetTopicBundleAsync` returns a synthetic bundle — one pseudo-article
`{Title = Label, Text = EditorInput}` — when the topic is Manual. Everything downstream is the
normal pipeline: style-guide generation, `DraftValidator`, self-check (checks claims against the
editor's text — exactly right), image suggestions, `SaveDraftAsync` (clears `ForceDraftAtUtc`,
one-shot), review card.

**Failure visibility:** normal topics fail silently (they retry / give up), but an editor is
actively waiting on `/new`. `RecordGenerationFailureAsync` already inserts a `GenerationFailed`
draft row carrying the error; the notice query (`GetUnreportedRegenFailuresAsync`) currently
filters those on `RegenInstructions IS NOT NULL` — extend its WHERE with
`OR t.Status = 'Manual'` and TelegramJob posts the existing
`⚠️ Статията не можа да се генерира: <error>` notice once (message-id stamping already marks it
reported without starting a review clock). Transient AI-quota failures keep retrying as today.

### `/post <text>` — verbatim path

`CreateManualArticleAsync(headline, body)`, one transaction:

1. INSERT `nw_Topic` as above (`EditorInput=text` so a later ✏️ regen has source material;
   **no** `ForceDraftAtUtc` — nothing to generate).
2. INSERT `nw_Draft`: `Status='PendingReview'`, `Version=1`, `Headline` = first non-empty line,
   `BodyMarkdown` = remainder (may be empty → headline-only post), `ModelName='editor'`,
   zero cost/tokens, no images.
3. The existing dispatch loop posts the review card; ✅ publishes, photo-reply attaches an image,
   ✏️ regenerates **via AI** (costs one Draft request — the only AI touch a `/post` article can
   have, and only if the editor asks).

Published FB-only post = `{headline}\n\n{ComposeFullBody(body)}` — the current whole-body
composition; line breaks preserved as authored (existing behaviour).

### Routing (`ReviewUpdateRouter.RouteText`)

- `/post` and `/new` take **free text**, not an id: the argument is everything after the command
  token, whitespace-trimmed, line breaks preserved. Empty → `Ignore(ReasonBadArguments)`, which
  the worker skips silently — the existing behaviour for every bad-args command (verified: there
  is no usage-hint reply seam; `/help` documents usage instead).
- Command tokenisation must accept a **newline** right after the command (`/post\nЗаглавие…`):
  today's space-only split would treat that as unknown text.
- `/post`: first non-empty line of the argument = headline (works whether the headline shares
  the command's line or starts on the next one); remainder = body.
- New `ReviewCommand` records: `CreateArticle(string Headline, string Body)` (verbatim),
  `CreateAiArticle(string Text)`.
- `@BotName` suffix handling and allowlist gating apply unchanged.

### Review card for manual topics

The card header currently reads `🔥 <label> (score 8.2, 5 източника)`. For Manual topics render
`✍️ <label> (редакторска)` instead — no score, no source links. Null category/region/tags on
verbatim drafts render as today's empty-field behaviour.

### Lifecycle notes

- Review TTL applies unchanged — an unreviewed manual draft expires like any other.
- Manual topics are one-shot: `SaveDraftAsync` clears `ForceDraftAtUtc` (`/new`) and `/post`
  never sets it, so nothing regenerates unless the editor taps ✏️.
- `/quota` already shows Draft-stage usage; `/new` and ✏️ count there like any draft.
- `/help` gains two lines for the new commands.

## Testing (TDD; no DB test harness — SQL is build-verify + manual UAT)

1. **Router (pure, write first)** — `ReviewUpdateRouterTests`:
   - `/post Заглавие\nтяло` → `CreateArticle("Заглавие", "тяло")`; headline-on-next-line and
     single-line variants; `/post` / `/post   ` → `Ignore(BadArguments)`.
   - `/new бележки...` → `CreateAiArticle(...)` preserving line breaks; empty → bad args.
   - `@BotName` suffix variants still route.
2. **Formatter (pure)** — manual-topic card header (`✍️ … (редакторска)`), `/help` includes the
   new commands.
3. **Manual UAT** — `/post` end-to-end to the FB page (DryRun first), `/new` end-to-end incl. a
   ✏️ change request on each kind, photo-reply attach, generation-failure notice.

## Docs

- `docs/05-integrations/telegram.md`: add `/post`, `/new` to the command table + manual-card
  format note.
- `docs/02-functional-spec.md` §5: editor-authored articles paragraph.
- `docs/11-risks-and-open-questions.md`: manual drafts need category/SEO defaults before Umbraco
  publishing re-enables.
- `docs/decision-log.md`: one line for the feature decision.

## Files touched

- `src/Newsroom.Core/Trends/TopicStatus.cs` (add `Manual`)
- `src/Newsroom.Core/Review/ReviewCommand.cs` + `ReviewUpdateRouter.cs`
- `src/Newsroom.Core/Review/Interfaces.cs` / `Drafting/Interfaces.cs` (create methods)
- `src/Newsroom.Infrastructure/Repositories/ReviewRepository.cs` (create methods, card view)
- `src/Newsroom.Infrastructure/Repositories/DraftRepository.cs` (`GetTopicBundleAsync` synthetic
  bundle for Manual topics)
- `src/Newsroom.Infrastructure/Database/Migrations/0012_manual_topics.sql`
- `src/Newsroom.Worker/Jobs/TelegramJob.cs` (two command cases, failure notice, card header,
  `/help` text)
- Tests: `ReviewUpdateRouterTests` + formatter tests
- Docs listed above
