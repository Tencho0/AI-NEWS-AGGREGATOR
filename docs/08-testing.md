# 08 — Testing Strategy

**Status:** Draft · **Last updated:** 2026-08-04

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
| Sandbox end-to-end (manual) | `tools\restart-sandbox.ps1` + a local Umbraco site (ADR-0014) | The manual end-to-end harness for this dev machine — proves a draft can be approved via real Telegram review and land on the local site, with Facebook forced to dry-run, without any path to the live database, site or Facebook page |

## Sandbox end-to-end harness

The sandbox (ADR-0014) is this dev machine's manual end-to-end harness — it is the only way to
exercise the whole pipeline (real scraping, real AI, real Telegram review) locally without any path
to the live database, the live site or the real Facebook page. Procedure, with the local Umbraco
site running and `tools\restart-sandbox.ps1` started:

1. Wait for a review card in the sandbox chat. It must be prefixed `🧪 SANDBOX`.
2. Tap ✅ Одобри.
3. Check the sandbox log for the Umbraco publish success line and open the returned URL — it must
   be on `https://localhost:44350`.
4. Check the sandbox log for `Facebook dry run for draft {id}` and confirm there is **no** Graph
   API call and nothing new on the real page.
5. Confirm the live worker (the VPS Windows Service, [09-deployment.md](09-deployment.md)) is
   unaffected — this dev machine never runs it, so there is nothing here to interfere with.

**Status: executed successfully on 2026-08-04.** Full pipeline on the dev machine — 110 articles
scraped from the 6 seeded sources, 80 analysed, 57 topics clustered, 4 drafts generated, all 4
review cards delivered to the sandbox bot prefixed `🧪 SANDBOX`. One draft was approved and one
rejected (both transitions recorded in `nw_ReviewAction`); the approved one published to
`https://localhost:44350/novini/...` and returned HTTP 200. `nw_PublishRecord` held zero `facebook`
rows throughout, and the live VPS pipeline was untouched.

Two defects surfaced that no review had caught, both now fixed:

- The first-time-setup order was wrong — it said to seed the sources straight after
  `CREATE DATABASE`, but the schema is applied by the worker's migration runner at startup, so the
  seed failed with `Invalid object name 'dbo.nw_Source'`.
- Both restart scripts tailed the log with a bare `Get-Content`, which Windows PowerShell 5.1
  decodes as ANSI, so Bulgarian source names and the banner rendered as mojibake.

One prerequisite is easy to miss and is now called out in the runbook: **the ASP.NET Core HTTPS
development certificate must be trusted** (`dotnet dev-certs https --trust`). Without it the
publish leg fails with `The SSL connection could not be established` — correctly classified as
transient, retried to `Umbraco:MaxAttempts` and then parked as `PublishFailed` with the real reason
preserved in `nw_PublishRecord.Error`. Note that verifying the endpoint with `curl -k` does **not**
catch this, because it skips the very check that fails.

Automated coverage for the isolation mechanism itself (not the end-to-end flow, which is manual by
design): `SandboxOptionsTests` (22 cases — the fail-closed startup guard's `Violations` logic, plus
the `Sandbox:Enabled` master switch that gates the guard, both forced overrides and the Telegram
marker) and `SandboxTelegramGatewayTests` (7 cases — the 🧪 SANDBOX message prefix).

## CI (GitHub Actions)

- On PR: build, unit + infrastructure-fixture tests, formatting/analyzers.
- DB integration tests: nightly or on demand (needs SQL service container `mcr.microsoft.com/mssql`).
- No secrets in CI for v1 (no live-API tests in CI at all).

## Definition of Done (every phase)

Code + tests green + docs updated (spec/ADR) + smoke test of the affected slice + entry in
`10-roadmap.md` progress log.
