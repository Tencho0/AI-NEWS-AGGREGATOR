# ADR-0006 — Telegram bot via long polling as the review surface

**Status:** Proposed · **Date:** 2026-07-02

## Context

Human approval is mandatory before publishing. The editor needs a mobile-friendly, push-based
review surface. Telegram is required by the product brief; the open choice is webhook vs long
polling, and Telegram-vs-anything-else for v1.

## Options considered

1. **Long polling (`getUpdates`) from the worker** — no inbound endpoint, no public URL, no TLS
   binding or IIS involvement for the bot; state = one offset row. Slightly higher idle traffic
   (negligible at this scale).
2. **Webhook** — needs a public HTTPS endpoint (new IIS site/route + cert coupling to the worker),
   more moving parts on the VPS for zero benefit at one-chat volume.
3. Web review UI instead of Telegram — bigger build, no push notifications; rejected for v1
   (revisit in backlog).

## Decision

Option 1 — `Telegram.Bot` with long polling, allowlisted user ids + chat ids as the authorization
model, offset + pending conversations persisted in `nw_TelegramState`.

## Consequences

Simplest secure setup on the existing VPS; the review surface is availability-coupled to Telegram
(accepted — drafts queue safely in the DB when Telegram is unreachable). If a second interface is
ever needed, the approval state machine already lives in Core, not in the bot.
