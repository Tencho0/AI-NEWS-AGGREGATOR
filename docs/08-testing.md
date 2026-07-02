# 08 — Testing Strategy

**Status:** Draft · **Last updated:** 2026-07-02

## Principles

- Every stage is behind an interface → unit-testable with fakes; no test ever calls a paid API or
  a live site by default.
- Determinism first: the state machine, trend scoring, dedup and idempotency logic are pure logic
  in `Newsroom.Core` — they get the densest tests.
- AI output is non-deterministic → we test the **contract** (schema validity, our validation
  gates), and keep a small curated **golden set** for manual eval, not CI assertions on prose.

## Test layers

| Layer | Tooling | What is covered |
|---|---|---|
| Unit (Core) | xUnit + NSubstitute | Draft state machine (every transition + illegal ones), trend scoring, dedup/canonicalisation, cost-cap logic, Telegram command parsing, prompt templating (snapshot via Verify) |
| Unit (Infrastructure) | xUnit | HTML/RSS extraction against saved fixture files per source (real captured pages, committed), markdown mapping, Graph API request shapes |
| Integration (DB) | xUnit + local SQL Express (`Newsroom_Test` db) | Repositories, migrations from scratch, idempotent upserts, crash-recovery resets |
| Contract (Umbraco publishing) | Shared JSON schema + tests on both repos | Worker serialises exactly what the endpoint deserialises; endpoint integration test in Predel-News creates+publishes a real article on a dev database and asserts URL/slug/media |
| Integration (Telegram) | Manual test-bot + a `FakeTelegramGateway` for automated flows | Full review conversation incl. change-requests, idempotent callbacks, unknown-user rejection |
| AI eval (manual, per prompt version) | Golden set: ~10 real topic bundles → generate → editor scores rubric (accuracy, originality, style, Bulgarian quality) | Run before any prompt-version or model change ships; results noted in decision-log.md |
| End-to-end smoke | Staging config on the VPS (test Telegram chat, Umbraco dev site, FB test page or dry-run flag) | One article through the whole pipeline before each release |

## CI (GitHub Actions)

- On PR: build, unit + infrastructure-fixture tests, formatting/analyzers.
- DB integration tests: nightly or on demand (needs SQL service container `mcr.microsoft.com/mssql`).
- No secrets in CI for v1 (no live-API tests in CI at all).

## Definition of Done (every phase)

Code + tests green + docs updated (spec/ADR) + smoke test of the affected slice + entry in
`10-roadmap.md` progress log.
