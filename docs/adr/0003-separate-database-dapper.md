# ADR-0003 — Separate `Newsroom` database on the existing SQL Express instance; Dapper

**Status:** Proposed · **Date:** 2026-07-02

## Context

The pipeline needs durable state (articles, topics, drafts, audit, cost ledger). The VPS already
runs SQL Server Express 2022 for the Umbraco site. The site's own custom tables use Dapper with
versioned migrations.

## Options considered

1. **New database `Newsroom` on the existing instance, Dapper + SQL migrations** — full isolation
   from the Umbraco DB (no accidental coupling, independent restore), zero new infrastructure,
   mirrors the developer's existing patterns.
2. Same tables inside the Umbraco DB (`nw_` prefix) — one backup unit, but couples lifecycles and
   risks accidental cross-dependencies; Umbraco upgrades and restores get riskier.
3. SQLite file — simplest, but weaker concurrency for parallel jobs, no shared tooling with the
   site, and SQL Express is already there.
4. EF Core instead of Dapper — migrations for free, but diverges from existing patterns and adds
   abstraction the simple schema doesn't need.

## Decision

Option 1. Tables prefixed `nw_`, UTC timestamps, forward-only backward-compatible migrations
applied at worker startup.

## Consequences

Clean separation from the site's data; must ensure the new DB is included in the backup regime
(open question Q-5); the database doubles as the pipeline's queue (statuses), which keeps the
architecture crash-tolerant without extra messaging infrastructure.
