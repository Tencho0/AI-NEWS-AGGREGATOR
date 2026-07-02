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

- **Predel-News** — the existing Umbraco 17 website (publishing target). Receives one companion
  change: an authenticated publishing endpoint, specified in
  [docs/05-integrations/umbraco.md](docs/05-integrations/umbraco.md).
