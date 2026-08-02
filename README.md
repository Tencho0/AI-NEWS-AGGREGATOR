# AI-NEWS-AGGREGATOR

AI-powered newsroom automation for **Predel News**: monitor Bulgarian news sources → detect
trending topics → draft original articles with suggested images → human approval in Telegram →
automatic publishing to the Facebook Page and the Umbraco website.

## Start here

📚 **[docs/README.md](docs/README.md)** — the full evolving project plan: vision & scope,
functional and technical specs, architecture, integrations, security, operations, testing,
deployment, roadmap, risk register, and the ADR decision log.

This project is documentation-first: every important decision is recorded as an ADR in
[docs/adr/](docs/adr/) before (or with) the code that implements it.

## Status

Planning phase (Phase 0). Current milestone: confirm proposed ADRs 0002–0009 and the open
questions in [docs/11-risks-and-open-questions.md](docs/11-risks-and-open-questions.md),
then scaffold the solution per [docs/10-roadmap.md](docs/10-roadmap.md).

## Related repositories

This repo is **only the automation**. The website it publishes to is a separate repo:

| Repo | What it is | Relationship |
|---|---|---|
| **AI-NEWS-AGGREGATOR** — `Tencho0/AI-NEWS-AGGREGATOR` *(this repo)* | .NET worker: monitors sources, detects topics, drafts articles + covers, runs the Telegram approval flow | Publishes **into** Predel-News |
| **Predel-News** — `Tencho0/Predel-News` | The public site: Umbraco 17 / .NET 10, Bulgarian regional news for the Blagoevgrad region | Hosts `NewsroomPublishingApiController`, the authenticated endpoint this worker posts to — contract in [docs/05-integrations/umbraco.md](docs/05-integrations/umbraco.md) |

The two share a VPS ([docs/09-deployment.md](docs/09-deployment.md)) and an owner, but version and
deploy independently: a change to the publishing contract needs an ADR **here** and a PR **there**.

### Finding the other repo

Identify it by its git remote — `git@github-personal:Tencho0/Predel-News.git` — **not** by a path.
Checkout locations move. By convention the two clones sit side by side:

```
<your-source-root>/
├── AI-NEWS-AGGREGATOR/   ← this repo
└── Predel-News/          ← the website
```

If it isn't next to this one, `git remote -v` in any clone settles it. Docs in this repo say "the
Predel-News repo" and use paths relative to *its* root (e.g. `src/BackofficeExtensions/`) — never
absolute machine paths, so nothing here breaks when the folders move.
