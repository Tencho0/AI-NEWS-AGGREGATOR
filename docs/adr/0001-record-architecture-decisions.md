# ADR-0001 — Record architecture decisions

**Status:** Accepted · **Date:** 2026-07-02 · **Deciders:** project owner

## Context

The project mandates strict documentation of every important decision (architecture, technology,
integrations, API design, data flow, workflows, security, deployment). Decisions scattered across
chats and commit messages don't survive; a lightweight, append-only format does.

## Decision

Use Architecture Decision Records (MADR-style, one file per decision) in `docs/adr/`, indexed in
`docs/decision-log.md`, following the process in `docs/adr/README.md`. Decisions are immutable
once accepted; reversals are new superseding ADRs.

## Consequences

Small constant overhead per decision; complete, auditable decision history; onboarding and future
debugging ("why is it like this?") become cheap.
