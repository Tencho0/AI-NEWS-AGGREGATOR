# Integration — AI Content Generation (provider-agnostic; Gemini default)

**Status:** Draft · **Last updated:** 2026-07-02 · **ADR:** 0010 (supersedes 0005)

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
| Rate limits | ~15 RPM, ~1,500 requests/day (Flash); Flash-Lite ~2× RPM | Client-side throttle (token bucket) in the AI layer; jobs queue, never drop |
| Batch API | Not on free tier | Batch by **packing** N articles into one analysis request instead |
| Data usage | Free-tier content may be used by Google to improve products | Only public news content is sent — never secrets/credentials/personal data (06-security.md) |
| SLA | None | Retries + graceful degradation are mandatory (below) |

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
