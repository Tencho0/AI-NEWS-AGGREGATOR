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
| 2026-07-03 | — | Q-2 style guide drafted from the owner's sample articles (`docs/editorial-style-guide.md`, PROMPT-START/END block is the prompt source, single-sourced into Core as an embedded resource); Phase 3 proceeds on the draft, owner review pending | Proposed |
| 2026-07-03 | — | Publishing contract v1 (owner may veto): automated articles use a dedicated **"Predel News" staff author** (resolves Q-6); `coverImage` falls back to a **configured placeholder media item** when the draft has no image (stock keys pending); tags are find-or-create by name; category/region resolved by node name | Proposed |
| 2026-07-03 | — | Facebook v1 semantics: DryRun default ON (staging mode; completes flow as "(пробен режим)" success); FB failures never block/roll back the site publish — draft sits `PartiallyPublished` with retries until the page post lands; OAuth errors terminal + daily token health probe | Noted |
| 2026-07-03 | — | **Q-1 sources onboarded** (owner list): БТА `/bg/rss/free`, Mediapool `/rss`, Струма `/feed`, Топ Преса `/feed`, ИнфоМрежа `/rss`, Благоевград24 `/rss/` — feeds verified with the bot UA 2026-07-03; robots.txt enforced at runtime by the scraper. Deferred: pirinsko.com (no RSS), blagoevgrad.bg (sitemap only) → backlog sitemap/html adapters. 48 h M1 collection run started 2026-07-03 ~21:47 (detached worker `C:\apps\newsroom-dev`, publishing leg dormant) | Accepted |
