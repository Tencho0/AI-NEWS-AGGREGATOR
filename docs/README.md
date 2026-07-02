# AI Newsroom Automation — Documentation

This folder is the **single source of truth** for the project. The plan is not a static document —
it is this tree of living documents plus an append-only decision log (ADRs). Code follows docs,
not the other way around.

## Documentation map

| Doc | Contents | Update trigger |
|---|---|---|
| [01-vision-and-scope.md](01-vision-and-scope.md) | Vision, goals, scope / non-scope, success metrics | Scope change |
| [02-functional-spec.md](02-functional-spec.md) | Pipeline behaviour, approval state machine, user-facing flows | Any behaviour change |
| [03-architecture.md](03-architecture.md) | System architecture, module breakdown, data flow, repo structure | Any structural change |
| [04-technical-spec.md](04-technical-spec.md) | Stack, projects, data model, storage | Schema / stack change |
| [05-integrations/](05-integrations/) | One doc per external system (scraping, AI, Telegram, Facebook, Umbraco, images) | Integration change |
| [06-security.md](06-security.md) | Secrets, auth, data protection, threat notes | Any security-relevant change |
| [07-operations.md](07-operations.md) | Logging, monitoring, error handling, runbook | Ops change |
| [08-testing.md](08-testing.md) | Testing strategy per layer | New test category |
| [09-deployment.md](09-deployment.md) | Deployment strategy, environments, rollback | Deploy process change |
| [10-roadmap.md](10-roadmap.md) | Phases, milestones, task breakdown | End of every phase |
| [11-risks-and-open-questions.md](11-risks-and-open-questions.md) | Risk register + open questions awaiting a decision | Continuously |
| [adr/](adr/) | Architecture Decision Records (append-only) | Every important decision |
| [research/](research/) | Dated research notes feeding future decisions (no normative force) | When investigating options |
| [decision-log.md](decision-log.md) | Index of all ADRs + lightweight decisions | Every decision |

## Documentation-first workflow

The rule: **no important decision exists until it is written down.**

1. **Before implementing a phase** — re-read the relevant spec sections; write/update the phase's
   detailed task list in `10-roadmap.md`; resolve any open questions that block the phase (each
   resolution becomes an ADR or a decision-log entry).
2. **When a decision is needed** — write an ADR (see [adr/README.md](adr/README.md)): context,
   options considered, decision, consequences. Status `Proposed` until confirmed, then `Accepted`.
   Add one line to `decision-log.md`.
3. **When reality diverges from a doc** — fix the doc in the same PR/commit as the code change.
   A doc that lies is worse than no doc.
4. **When a decision is reversed** — never edit the old ADR; write a new one that supersedes it
   and mark the old one `Superseded by ADR-XXXX`.
5. **Small decisions** that don't warrant a full ADR (a library version, a naming choice) get a
   one-line entry directly in `decision-log.md`.

## Conventions

- Each doc has a `Status` / `Last updated` header. `Draft` → `Agreed` → (kept current forever).
- Dates are ISO (`2026-07-02`).
- Bulgarian is the content language of the product; documentation is in English.
- ADR numbering is global and sequential (`0001`, `0002`, …), never reused.
