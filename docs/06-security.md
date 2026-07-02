# 06 — Security & Legal

**Status:** Draft · **Last updated:** 2026-07-02

## Secrets

| Secret | Used by | Storage |
|---|---|---|
| AI provider API key(s) (Gemini; others if configured per ADR-0010) | Worker | Windows Service environment variable / protected `appsettings.Production.json` (ACL: service account only). Never in git, DB, or logs. |
| Telegram bot token | Worker | same |
| Facebook Page access token | Worker | same; rotated per Meta expiry, health-checked daily |
| Umbraco API-user client secret | Worker | same |
| Stock image API keys | Worker | same |

Rules: repo contains only `appsettings.json` with placeholders; a `.gitignore`d
`appsettings.Production.json` template documented in 09-deployment.md; secrets redacted from all
log output (Serilog destructuring policy); rotation runbook in 07-operations.md.

## Authentication & authorization boundaries

- **Telegram**: allowlist of editor user ids + fixed chat ids; all other updates dropped and
  logged. Button callbacks validated against draft state (idempotent). The bot token itself is the
  second factor — anyone with the token could impersonate the bot, hence secret handling above.
- **Umbraco publishing endpoint**: dedicated API user with least privilege (News section only),
  client-credentials flow, HTTPS only. Idempotency keys prevent replay-duplication.
- **Facebook**: Page token scoped to the single page and minimal permissions.
- **Worker**: runs as a dedicated low-privilege Windows account; DB login has rights only on the
  `Newsroom` database (never the Umbraco DB).

## Input trust model

Scraped content is **untrusted input**:
- It flows into AI prompts → prompt-injection risk ("ignore your instructions and…" embedded in a
  scraped page). Mitigations: source text is clearly delimited as data in prompts; drafts always
  pass the human gate; the self-check flags anomalies; no tool-use/agentic capabilities are given
  to generation calls (pure text in → JSON out).
- It is never rendered as HTML anywhere internal without encoding; body reaches Umbraco as
  markdown converted server-side by the publishing endpoint.
- Parser hardening: size caps on downloads (2 MB HTML), content-type checks, no following of
  redirects off-domain more than 2 hops.

## Legal & editorial compliance

- **Copyright (text):** generated articles must be original synthesis with attribution;
  storing extracted text internally for analysis with limited retention (90 days).
- **Copyright (images):** hard rule — no scraped images; licence recorded per image (ADR-0009).
- **GDPR:** system stores editors' Telegram ids (legitimate interest, internal), and public
  news content. No reader data. Source articles may contain personal data → retention limits
  and no secondary use beyond journalism exemptions.
- **Media law / responsibility:** the approving editor is the accountable publisher of record;
  the audit trail (`nw_ReviewAction`) preserves who approved what and when.
- **AI free-tier data usage:** Gemini free-tier content may be used by Google to improve its
  products (ADR-0010). Accepted because only *public news content* is sent to the AI layer —
  never credentials, internal documents, or personal data beyond what the sources themselves
  published. Re-verify this term if the pipeline ever processes non-public material; paid tier
  removes the clause.
- **Meta ToS:** no group automation, no user-account automation (ADR-0008).

## Threats considered (summary)

| Threat | Mitigation |
|---|---|
| Prompt injection via scraped page | Data/instruction separation, human gate, self-check |
| Compromised bot token → fake approvals | User-id allowlist (token alone is insufficient), audit log, secret rotation |
| Publishing endpoint abuse | Client-credentials auth, least-privilege API user, rate cap, payload validation |
| Cost/quota blow-up (runaway loop / model misuse) | Daily request + cost budgets checked in `nw_CostLedger` before every call; per-stage token limits; client-side RPM throttle |
| Duplicate publishes on retry | Idempotency keys end-to-end (draft id → externalRef → FB record) |
| VPS compromise | Standard hardening is site ops scope; worker adds no inbound ports (long polling only) |
