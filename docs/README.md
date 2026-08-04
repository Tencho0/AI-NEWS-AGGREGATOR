# AI Newsroom Automation — Documentation

This folder is the **single source of truth** for the project. The plan is not a static document —
it is this tree of living documents plus an append-only decision log (ADRs). Code follows docs,
not the other way around.

## Companion repository

These docs cover the **newsroom automation** only. The site it publishes to — **Predel-News**
(`Tencho0/Predel-News`), an Umbraco 17 / .NET 10 news website — is a separate repo with its own
docs, tests and deploy cycle. Documents here call it "the Predel-News repo" and give paths relative
to *its* root. See [Related repositories](../README.md#related-repositories) for how to locate it
(by git remote, not by a fixed folder — checkout paths change) and
[05-integrations/umbraco.md](05-integrations/umbraco.md) for the contract between the two.

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
| [pipeline-flows.md](pipeline-flows.md) | End-to-end flow diagrams per pipeline stage | Any flow change |
| [editorial-style-guide.md](editorial-style-guide.md) | House style the AI drafting prompts must produce | Prompt / tone change |
| [runbooks/](runbooks/) | **Step-by-step operational procedures** — see the table below | Any operational change |
| [adr/](adr/) | Architecture Decision Records (append-only) | Every important decision |
| [research/](research/) | Dated research notes feeding future decisions (no normative force) | When investigating options |
| [decision-log.md](decision-log.md) | Index of all ADRs + lightweight decisions | Every decision |

## Runbooks — what to open when

The documents above describe how the system is *designed*. These describe what to *do*, with the
real paths and commands for the live host. Reach for these first when operating the system.

| Runbook | Open it when |
|---|---|
| [release-a-new-version.md](runbooks/release-a-new-version.md) | **Start here for any deploy.** Records the live environment — every path, account, database and config file as measured — plus the exact build/copy/deploy sequence for *both* the site and the worker, the rollback story, and the host's traps (PowerShell 4.0, path length, Cloudflare TLS, disk space). |
| [deploy.md](runbooks/deploy.md) | The worker's general deploy procedure and first-time install behind the above. |
| [rollback.md](runbooks/rollback.md) | A release misbehaves and you need the previous binaries back. |
| [move-to-a-new-host.md](runbooks/move-to-a-new-host.md) | Moving both applications to a different server. Inventories all thirteen pieces of state that do not travel with the code, and the order to rebuild them. Needed before Server 2012 R2's ESU ends 2026-10-13. |
| [start-the-worker.md](runbooks/start-the-worker.md) | Getting the worker running locally for development. |
| [restore-after-vps-restart.md](runbooks/restore-after-vps-restart.md) | The host rebooted and things did not come back. |
| [add-a-source.md](runbooks/add-a-source.md) | Adding a news feed to monitor. |
| [add-a-public-figure.md](runbooks/add-a-public-figure.md) | Registering a reference photo so covers can depict a named person. |
| [facebook-token-renewal.md](runbooks/facebook-token-renewal.md) | Facebook publishing starts failing on auth — the Page token expired. |
| [cost-cap-hit.md](runbooks/cost-cap-hit.md) | The AI budget or a provider quota is exhausted mid-day. |
| [google-search-console.md](runbooks/google-search-console.md) | SEO / indexing work: sitemap submission, coverage checks. |

The **website** has its own docs in the Predel-News repo — `docs/technical/deployment.md` for its
deployment design, `docs/technical/architecture.md`, `docs/business/`. Its `tools/` folder holds
`preflight.ps1` and `sql-check.ps1`, which verify a host before anything is deployed to it.

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
