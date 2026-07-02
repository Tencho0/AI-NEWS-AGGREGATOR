# Architecture Decision Records

Every important decision — architecture, technology, integration, API contract, data flow,
approval workflow, publishing logic, error handling, security, deployment — is recorded here as
an ADR. This satisfies the project's strict documentation policy and gives every future reader
(including future us) the *why*, not just the *what*.

## Process

1. Copy [template.md](template.md) → `NNNN-short-kebab-title.md` (next free number, global,
   never reused).
2. Fill in Context / Options / Decision / Consequences. Keep it under a page.
3. Status lifecycle: `Proposed` → `Accepted` (or `Rejected`) → possibly `Superseded by ADR-NNNN`.
   **Never edit an accepted ADR's decision** — write a superseding one.
4. Add one line to [../decision-log.md](../decision-log.md).
5. Reference the ADR from the affected spec doc ("see ADR-0007").

Small decisions that don't warrant an ADR get a one-liner directly in the decision log.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-dotnet-worker-stack.md) | .NET 10 worker service as the platform | Proposed |
| [0003](0003-separate-database-dapper.md) | Separate `Newsroom` DB on existing SQL Express, Dapper | Proposed |
| [0004](0004-in-process-scheduling.md) | In-process hosted-service scheduling (no Quartz/Hangfire in v1) | Proposed |
| [0005](0005-anthropic-claude-for-generation.md) | Anthropic Claude for analysis & generation; model-per-stage | Rejected → 0010 |
| [0006](0006-telegram-long-polling.md) | Telegram bot via long polling as the review surface | Proposed |
| [0007](0007-umbraco-publishing-endpoint.md) | Dedicated authenticated publishing endpoint inside the Umbraco site | Proposed |
| [0008](0008-facebook-page-only.md) | Facebook: Page publishing only; no group automation | Proposed |
| [0009](0009-image-sourcing-rules.md) | Image sourcing rules (no scraped images) | Proposed |
| [0010](0010-provider-agnostic-ai-gemini-default.md) | Provider-agnostic AI layer; Gemini (free tier) as default provider | Accepted |
