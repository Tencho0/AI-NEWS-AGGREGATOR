# Integration — AI Content Generation (provider-agnostic; Gemini default)

**Status:** Draft · **Last updated:** 2026-07-10 · **ADR:** 0010 (supersedes 0005)

## Architecture: two layers, providers are plugins

```
 Pipeline jobs ──▶ IAiClient (domain operations, ours)
                    ├─ SummariseAndClassifyAsync(articles)      → AnalysisResult (JSON schema)
                    ├─ ClusterAssistAsync(candidates)           → ClusterResult
                    ├─ DraftArticleAsync(topicBundle)           → DraftResult
                    ├─ RegenerateAsync(draft, instructions)     → DraftResult
                    └─ SelfCheckAsync(draft, sources)           → SelfCheckResult
                              │
                              ▼
                Microsoft.Extensions.AI.IChatClient  (the .NET-standard provider seam)
                    ├─ Google.GenAI adapter          ← default (Gemini, free tier)
                    ├─ Anthropic SDK adapter         ← optional, config switch
                    └─ (OpenAI/Azure/Ollama adapters exist in the ecosystem)
```

- Provider **and** model are configured **per stage** (`Ai:Stages:Draft:Provider/Model`); no
  provider types leak above `IChatClient`. Switching provider = config change (+ NuGet adapter
  registration if it's a brand-new provider).
- Prompts are provider-neutral: plain system + user text, JSON-schema-constrained outputs
  (Gemini `responseSchema` / structured output; equivalents exist on other providers).
- Model ids are **configuration, not code** — the Gemini catalog moves fast.

## Default provider: Gemini API free tier (ADR-0010)

Facts as of 2026-07 (indicative — the authoritative live numbers are in the
[AI Studio dashboard](https://aistudio.google.com/rate-limit); re-check when tuning):

| Constraint | Value | Design consequence |
|---|---|---|
| Models on free tier | **Flash tier only** (Pro is paid-only since 2026-04) | All stages start on the current Flash model; drafting quality gate decides upgrades |
| Rate limits | RPM throttled client-side (shared `AiRateLimiter`, `Ai:RequestsPerMinute`); **RPD ≈ 20/day _per model_** on this project (exception: `gemini-3.1-flash-lite`, 500 — see *Free-tier limitations* below) — observed 2026-07-10, far below the ~1,500 general figure | One model per stage so each gets its own daily bucket; jobs queue on 429, never drop |
| Batch API | Not on free tier | Batch by **packing** N articles into one analysis request instead |
| Data usage | Free-tier content may be used by Google to improve products | Only public news content is sent — never secrets/credentials/personal data (06-security.md) |
| SLA | None | Retries + graceful degradation are mandatory (below) |

### Models per stage (current config, 2026-07-21)

Provider+model are per-stage config (`Ai:Stages:{stage}:Model`); these are the values live in
`src/Newsroom.Worker/appsettings.json`:

| Stage | Model | Batch | Why this model |
|---|---|---|---|
| **Analyse** (summarise/classify) | `gemini-3.1-flash-lite` | 8 articles/request | Highest volume; classification tolerates a lighter model. Own daily bucket (500 RPD). Also the daily-quota fallback model for the other stages (below). |
| **Cluster** (trend grouping) | `gemini-2.5-flash` | 30 candidates/request | Own daily bucket so a scraping burst on Analyse can't starve it. |
| **Draft** (article generation) | `gemini-3.5-flash` | 1 topic/request | Newest/strongest flash — drafting is the quality-critical Bulgarian generation. |
| **SelfCheck** (claim verification) | `gemini-3.5-flash` | 1 draft/request | Low volume; shares Draft's bucket. |

**Why one model per stage:** the free-tier daily quota is **per project, _per model_**
(`GenerateRequestsPerDayPerProjectPerModel-FreeTier`). Point every stage at one model and they all
draw from a single ~20/day allowance (they starve each other); give each bulk stage its own model
and each gets its own allowance. Analyse and Cluster each get a dedicated model; Draft + SelfCheck
(both low volume) share one. Model ids are config — re-tune as the Gemini catalog and quotas move.

### Daily-quota fallback (2026-07-21)

When Cluster, Draft, or SelfCheck gets a **daily**-quota 429 from Gemini (a quota id naming
`…PerDay…` — per-minute 429s keep the normal retry-next-cycle path), the stage automatically
switches to the Analyse stage's model until the quota reset (midnight US-Pacific), then switches
back. Implemented as a delegating `IChatClient` (`GeminiQuotaFallbackChatClient` +
`GeminiModelFallback` state) so jobs and adapters are untouched; the triggering request is
retried once on the fallback model, and `nw_CostLedger.Model` records the model actually used.
State is per **model**, so Draft and SelfCheck (same model) flip together; it is in-memory, so a
worker restart merely re-probes the primary (worst case: one extra 429 re-activates).

**Gemini-only:** a stage whose `Ai:Stages:{stage}:Provider` is not `gemini` (absent = `gemini`,
ADR-0010) is never wrapped, and the Analyse fallback target must be Gemini too — the fallback can
never switch a Claude/OpenAI stage's model. Stage `DailyRequestBudget`s still apply, so worst
case on the Analyse model is 450 + 18 + 9 + 9 = 486 requests/day, inside its 500 RPD.

### Free-tier limitations (observed on this project, 2026-07-10; updated 2026-07-21)

The general Gemini figures (~15 RPM / ~1,500 RPD) do **not** apply here. Measured against live
429s and the AI Studio quota:

- **~20 `generateContent` requests/day, per model** on the free tier for most text models —
  confirmed on `gemini-2.5-flash`, `gemini-3.5-flash`, and `gemini-2.5-flash-lite` (all report
  `limit: 20`, quota id `GenerateRequestsPerDayPerProjectPerModel-FreeTier`). The exception is
  **`gemini-3.1-flash-lite` at 500/day** (AI Studio dashboard) — which is why Analyse runs it
  and why it doubles as the daily-quota fallback target.
- **`gemini-2.0-flash` / `gemini-2.0-flash-lite` have _zero_ free quota** (`limit: 0`) — they
  appear in the model list but cannot be used free; never assign a stage to them.
- Quota **resets at midnight US-Pacific** (~10:00 Europe/Sofia).
- With `gemini-3.1-flash-lite` on Analyse the free tier no longer tops out at ~60 requests/day:
  Analyse alone has 500/day (budgeted 450 — up to ~3,600 analysed articles/day at 8 per
  request), while Cluster and Draft/SelfCheck stay on ~20/day models (budgets 18 and 9+9 —
  ~540 clustering candidates/day, ~9 draft cycles/day). Cluster and Draft remain the scarce
  buckets; the daily-quota fallback (above) borrows Analyse's headroom when they run dry.
- On quota exhaustion a stage logs `AI temporarily unavailable … will retry later` and resumes
  after the reset; no work is lost (the item's attempt is not burned — see
  `AiTransientErrors.IsQuotaExhausted`). On a **daily**-quota 429, Cluster/Draft/SelfCheck do
  not wait: they fall back to the Analyse model automatically ("Daily-quota fallback" above).
- **The only way to raise a model's own ~20/day cap is enabling billing** (paid tier) —
  negligible cost at this volume, and the primary remedy for risk R-11. The daily-quota
  fallback does not raise any cap — it only borrows Analyse's separate 500/day bucket.

Re-verify current limits in AI Studio → <https://aistudio.google.com/rate-limit> (per project), or
list the models a key can use via `GET https://generativelanguage.googleapis.com/v1beta/models?key=…`
(a list call — no generate-quota cost).

### Quota & budget management

- Every call logged to `nw_CostLedger` (provider, model, stage, tokens, **request count**, cost —
  cost is 0 on free tier but the meter stays on so a paid-tier switch is a config change).
- Daily **request budget per stage** (e.g. analysis may consume max 70 % of RPD) so a scraping
  burst can't starve drafting. Budget exhausted → stage pauses + ⚠️ Telegram alert; scraping
  continues unaffected.
- 429/`RESOURCE_EXHAUSTED` handling: respect retry hints, back off, requeue — items are never lost.

### Upgrade paths (in order, each a config change under ADR-0010)

1. **More free capacity** (parked — owner decision 2026-07-02: Gemini only at launch):
   register additional free-tier providers behind the same seam and route per stage with
   fallback-on-429 (quotas are per-provider, so capacity adds up). Reference shortlist if ever
   reopened: [research/2026-07-free-ai-providers.md](../research/2026-07-free-ai-providers.md)
   — Groq (analysis overflow), Mistral Experiment (drafting), BgGPT/INSAIT (Bulgarian-first).
2. Gemini **paid tier** — higher limits, Pro-tier models, no training on submitted data.
3. Different paid provider for specific stages (e.g. Claude/OpenAI for drafting) via the same
   `IChatClient` seam — decision driven by the golden-set eval + a decision-log entry.

## Prompting strategy (unchanged in substance from v1 plan)

Prompts are **versioned artifacts** (`Newsroom.Core/Prompts/*.md`, embedded resources); every
draft records `PromptVersion`, provider and model. Prompt changes = PR + decision-log entry +
golden-set eval (08-testing.md).

**Drafting prompt contract (v1 sketch):**
- System: role ("journalist at a Bulgarian regional news site"), the editorial style guide (Q-2),
  hard rules: write in Bulgarian; original synthesis only (never translate-copy a single source);
  attribute claims ("според БТА…"); no invented facts/quotes/numbers; conflicting sources —
  say so; thin information — short article rather than padding.
- Output (JSON schema): headline, subtitle, body (markdown), category (site's fixed list),
  region, tags (≤ 6), seoTitle, seoDescription, imageSearchQueries (2–3, English),
  confidence + statements needing verification.
- User content: topic's source articles (title, source, published-at, summary/full text),
  trend context, target length.

**Quality gates before a draft reaches Telegram:** schema-valid; body length in bounds;
category/region valid against site taxonomy; self-check pass ("claims not present in sources?" —
flags shown to the editor); Bulgarian-language sanity check.

## Flash-tier quality risk (watch item)

Long-form Bulgarian drafting on a Flash-tier model is the plan's biggest quality unknown.
The Phase-3 **golden-set eval** (≈10 real topic bundles scored by the editor) is the gate:
if quality misses the bar, escalate per the upgrade paths above — the abstraction makes that a
budget decision, not an engineering one. Tracked as risk R-12.

## Hallucination / responsibility stance (unchanged)

AI drafts are never auto-published; sources are listed in the review message; self-check flags
are highlighted; the approving editor is the accountable publisher of record.
