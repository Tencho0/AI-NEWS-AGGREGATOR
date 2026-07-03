# Decision Log

Append-only index of every decision. Full rationale for numbered entries lives in `adr/`.
Lightweight decisions (no ADR needed) are recorded here directly.

| Date | Ref | Decision | Status |
|---|---|---|---|
| 2026-07-02 | ADR-0001 | Record decisions as ADRs | Accepted |
| 2026-07-02 | ADR-0002 | .NET 10 worker service platform | Proposed |
| 2026-07-02 | ADR-0003 | Separate `Newsroom` DB on existing SQL Express; Dapper | Proposed |
| 2026-07-02 | ADR-0004 | In-process hosted-service scheduling for v1 | Proposed |
| 2026-07-02 | ADR-0005 | Anthropic Claude, model-per-stage (Opus 4.8 drafting / Haiku 4.5 analysis), Batch + caching + cost cap | Rejected → ADR-0010 |
| 2026-07-02 | ADR-0006 | Telegram long-polling bot as review surface | Proposed |
| 2026-07-02 | ADR-0007 | Dedicated publishing endpoint inside the Umbraco site | Proposed |
| 2026-07-02 | ADR-0008 | Facebook Page automation only; groups stay manual (API deprecated) | Proposed |
| 2026-07-02 | ADR-0009 | Image sourcing chain; never reuse scraped images | Proposed |
| 2026-07-02 | — | Content language: Bulgarian only (matches site) | Noted (assumption A-2) |
| 2026-07-02 | — | Human approval mandatory for every publish in v1 (no full-auto mode) | Noted (scope) |
| 2026-07-02 | ADR-0010 | Provider-agnostic AI layer via `Microsoft.Extensions.AI.IChatClient`; **Gemini free tier as default provider** (owner decision); quota budgets replace dollar caps as the primary limit | Accepted |
| 2026-07-02 | research/2026-07-free-ai-providers.md | Free-provider survey: shortlist Groq (analysis overflow) + Mistral Experiment (drafting candidate) + BgGPT/INSAIT (Bulgarian-first); GitHub Models dev-only; skip Cerebras/OpenRouter | Noted |
| 2026-07-02 | — | **Owner: stay with Gemini API only at launch** — research shortlist parked; no secondary providers wired; provider abstraction (ADR-0010) retained as the escape hatch | Accepted |
| 2026-07-02 | — | Gemini API key provisioned; stored via `dotnet user-secrets` (dev) / env var (VPS) — never in the repo; rotate when convenient (key transited chat) | Noted |
| 2026-07-03 | — | Trend v1 simplifications: one topic per article (unique link, no similarity scores); no Hot demotion; trend scoring is pure code (AI only assists clustering) — formula documented in 02-functional-spec.md and `TrendScorer` | Noted |
