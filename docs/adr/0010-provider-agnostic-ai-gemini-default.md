# ADR-0010 — Provider-agnostic AI layer; Google Gemini (free tier) as default provider

**Status:** Accepted · **Date:** 2026-07-02 · **Deciders:** project owner
**Supersedes:** ADR-0005 (rejected)

## Context

ADR-0005 proposed Anthropic Claude. The owner decided: use the **Gemini API because it has a free
tier**, and require that **switching between providers is easy**. Verified facts (2026-07):

- Google's official .NET SDK is **`Google.GenAI`** (NuGet) and ships a
  **`Microsoft.Extensions.AI.IChatClient`** adapter. Anthropic's and OpenAI's .NET SDKs expose
  the same `IChatClient` abstraction — it is the .NET-standard provider seam.
- Free tier (indicative, changes over time — authoritative numbers live in the AI Studio
  dashboard): Flash-tier models at ~15 RPM / ~1,500 requests per day; **Pro models are paid-only
  since April 2026**; Batch API is excluded from the free tier; free-tier content may be used by
  Google to improve its products.

## Options considered

1. **Two-layer abstraction: domain `IAiClient` over `Microsoft.Extensions.AI.IChatClient`,
   Gemini as default provider.** Stage-level operations (Summarise, Classify, Draft, SelfCheck)
   defined by us; each stage binds to a configured provider+model through `IChatClient` adapters.
   Prompts kept provider-neutral (plain system/user text + JSON-schema outputs).
2. Direct `Google.GenAI` usage everywhere — simplest today, but switching providers means
   touching every call site; violates the owner's requirement.
3. Hand-rolled provider interface without M.E.AI — reinvents an abstraction the ecosystem
   already standardised, and loses ready-made adapters/middleware (telemetry, function calling).

## Decision

Option 1.

- **Default provider: Gemini free tier**, Flash-tier model for all stages initially
  (model ids are configuration, not code).
- Provider/model **per pipeline stage** in config, e.g. `Ai:Stages:Draft:Provider=gemini`,
  `...:Model=<current flash model>`; adding a provider = one adapter registration.
- The AI layer owns **rate limiting** (client-side RPM throttle + daily request budget in
  `nw_CostLedger`) because free-tier quotas, not dollars, are the binding constraint.
- Free-tier privacy caveat accepted: only public news content is ever sent (no secrets, no
  personal data) — restated in 06-security.md.
- Upgrade paths kept open, in order of preference when quality or quota demands it:
  (a) Gemini paid tier (higher limits, Pro models, no training on data),
  (b) different provider per stage via the same abstraction (e.g. Claude for drafting).

## Consequences

Zero AI spend at launch; drafting quality on a Flash-tier model must be proven by the Phase-3
golden-set eval — if it falls short, the switch is a config change plus budget approval, not a
rewrite. The pipeline must tolerate quota exhaustion gracefully (throttle, queue, alert — new
risk R-11). Batch-discount assumptions from ADR-0005 are void; batching is instead achieved by
packing multiple articles into one analysis request. `nw_CostLedger` doubles as a request-quota
ledger.
