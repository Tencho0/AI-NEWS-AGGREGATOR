# ADR-0014 — Sandbox mode: a second worker instance isolated by environment, secrets, and a fail-closed guard

**Status:** Accepted · **Date:** 2026-08-04 · **Related:** full design in
`docs/superpowers/specs/2026-08-04-sandbox-mode-design.md`; builds on ADR-0007 (Umbraco publishing
endpoint) and ADR-0008 (Facebook Page only)

## Context

There is no development environment: this dev machine's own worker runs out of
`src\Newsroom.Worker\bin\Debug\net10.0`, started with `DOTNET_ENVIRONMENT=Development` — which is
exactly what loads the user-secrets store holding the real Telegram bot, the real Gemini key,
`Facebook:DryRun=false`, and the live page token. (The live pipeline itself runs as the Windows
Service `PredelNewsroom` on the Predel-News VPS, not on this dev machine — but this machine's
`Development` worker shares the *same* live Telegram bot token, so it is not a harmless local copy
either.) Any second run started for development today inherits all of it, plus this dev machine's
own `Newsroom` database and the shared default image root `%ProgramData%\PredelNewsroom\images`,
whose files a second instance's own `RetentionJob` would delete. The `Development` worker also
holds a lock on its own `bin\Debug` DLLs, so every `dotnet build` requires stopping it first. The
editor wants to develop against the full pipeline — real Telegram review cards, tap Approve — with
the article landing on a local Umbraco site and nothing whatsoever reaching the real Predel News
Facebook page.

## Options considered

1. **Config discipline alone** — rely on developers to point local config/secrets away from live
   by convention. Rejected: one copied secret (a token pasted from the wrong file, a forgotten
   override) reaches the live Facebook page, and nothing stops it.
2. **One-at-a-time profile swapping** — stop the live worker, swap environment/secrets, develop,
   swap back. Rejected: pauses the live pipeline for the whole development session, which is the
   problem the sandbox exists to remove.
3. **A stub publisher instead of a real local Umbraco** — fake out the publishing seam rather than
   running a real Predel-News site. Rejected: would not exercise the real
   `NewsroomPublishingApiController` contract, so a broken publish integration would only surface
   live.
4. **A separate `Sandbox` hosting environment, side by side with live** — its own committed
   config, its own user-secrets store that `Development` never loads, and a fail-closed startup
   guard that checks the effective destinations rather than trusting configuration. **Chosen.**

## Decision

Option 4. A `Sandbox` hosting environment runs side by side with live, isolated by:

- a **separate user-secrets store** that the live (`Development`) environment never loads and that
  holds no Facebook credentials at all;
- a **fail-closed startup guard** that refuses to run the pipeline when the database name does not
  end `_Sandbox`, the site URL is not `localhost`, or the image storage root does not contain
  `sandbox` — reporting every violation at once, not just the first;
- **forced overrides in code, not configuration**: `Facebook:DryRun` is always on and
  `Publishing:FacebookOnly` is always off for the sandbox, overriding whatever its own config says.

The live worker also moves from `bin\Debug\net10.0` to `C:\apps\newsroom`, so the two instances no
longer contend for locked DLLs.

## Consequences

A second BotFather bot is mandatory, not optional: Telegram long polling is per token, so two
pollers on one token would fight over `getUpdates`, each swallowing half the button presses. Both
instances share the Gemini free-tier key, so the sandbox consumes the live daily allowance;
contained by small per-stage `DailyRequestBudget` values, not eliminated. The guard protects
destinations, not the Telegram chat: the worker cannot recognise the editors' chat id, so a
sandbox pointed at it would still post there — visibly marked, but posted. In exchange,
`dotnet build` and F5 stop interrupting the live pipeline.
