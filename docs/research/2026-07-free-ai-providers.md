# Research Note — Free-tier AI providers beyond Gemini

**Date:** 2026-07-02 · **Status:** Parked (owner decision 2026-07-02: **Gemini only** — no
additional providers wired at launch; this note is reference material for the day quota, quality
or terms force a change per ADR-0010's upgrade paths)
**Purpose:** candidates for the secondary/fallback provider slots that ADR-0010's abstraction
makes cheap to add. Numbers below were verified 2026-07 but **free-tier limits churn constantly —
re-verify before wiring anything up.**

## Evaluation criteria

1. **Bulgarian output quality** (drafting is the hard case; analysis tolerates weaker models)
2. **.NET pluggability** — anything OpenAI-compatible plugs into our
   `Microsoft.Extensions.AI.IChatClient` seam via the official OpenAI .NET SDK with a custom
   endpoint; zero extra abstraction work
3. Real limits (RPM / RPD / TPM — TPM is often the binding constraint, not RPD)
4. Catches: data-training clauses, ToS restrictions, tier volatility

## Comparison (free tiers, as of 2026-07)

| Provider | Free limits | Models | Bulgarian | Integration | Catches |
|---|---|---|---|---|---|
| **Gemini** (default, ADR-0010) | ~15 RPM / ~1,500 RPD | Flash tier only | Good | Official `Google.GenAI` + IChatClient | Trains on free-tier data; Pro paid-only |
| **Groq** | 30 RPM; ~1K RPD on 70B-class, up to ~14.4K RPD on small models; **6–12K TPM is the real cap** | Llama 3.3 70B, gpt-oss-120b, more | Weak–moderate (open models underperform in BG) | OpenAI-compatible | No card needed; org-level limits; TPM chokes long prompts |
| **Mistral** (La Plateforme "Experiment") | ~**1B tokens/month**, but ~**2 RPM** | All Mistral models incl. Large | Moderate–good (EU-language focus) | OpenAI-compatible + official SDK | **Must opt in to data training**; 2 RPM needs queuing (fine at our volume) |
| **Cerebras** | 5 RPM / 30K TPM / 1M tokens/day | gpt-oss-120b, GLM-4.7 only | Weak–moderate | OpenAI-compatible | Limits cut in 2026-06 (volatility); tiny model choice |
| **OpenRouter** | 20 RPM / **50 RPD** free (1,000 RPD after $10 top-up) | Many `:free` variants (Llama, DeepSeek, Gemini-free) | Varies per model | OpenAI-compatible | 50 RPD too low for us without the $10 unlock; useful as a router later |
| **GitHub Models** | 10–15 RPM / 50–150 RPD / 8K-in 4K-out per request | GPT-4o-class, Claude, Llama, Phi | Strong (GPT-4o) | Azure AI Inference / OpenAI-compatible | **Prototyping-only ToS — not for production**; per-request token caps too small for drafting |
| **BgGPT 3.0 (INSAIT, Sofia)** | Open weights (permissive licence) on HuggingFace; free public chat; **org API access via INSAIT (terms unpublished — contact bggpt@insait.ai)** | 4B / 12B / 27B (Gemma 3 base), 131K context, vision | **Best-in-class Bulgarian** — 27B beats ~10× larger models on BG tasks | Self-host → OpenAI-compatible (vLLM/Ollama); or their API | Self-hosting needs a GPU (our VPS has none); API terms unknown → Q-9 |

## Recommendations (to adopt via config when needed — each adoption = decision-log entry)

1. **Groq as the overflow/fallback provider for the *analysis* stage** (summarise/classify).
   Independent quota pool from Gemini (roughly doubles free capacity), no card, trivially
   pluggable. Bulgarian weakness matters least in classification. Watch the TPM ceiling —
   send summaries, not full texts, when routed there.
2. **Mistral Experiment as the *drafting* fallback candidate.** 1B tokens/month dwarfs every
   other free quota; 2 RPM is compatible with our few-drafts-per-hour volume; Mistral Large's
   Bulgarian should be evaluated in the Phase-3 golden-set alongside Gemini Flash. Data-training
   opt-in is equivalent to what we already accepted for Gemini free (public news content only).
3. **BgGPT: pursue in parallel (Q-9).** A Bulgarian-first model from a Bulgarian public research
   institute is strategically ideal for drafting quality and a good story for the outlet. Email
   INSAIT about org API terms; if only self-hosting is offered it's blocked on GPU hardware we
   don't have (a cheap GPU cloud endpoint hosting BgGPT-27B would be its own cost decision).
4. **GitHub Models for dev/testing only** — run golden-set experiments against GPT-4o-class
   models for free to calibrate "how good could drafting be", but never wire it into production
   (ToS).
5. **Skip for now:** Cerebras (volatile limits, weak model fit), OpenRouter (50 RPD floor) —
   revisit if the landscape shifts.

## Multi-provider pattern this enables

Because quotas are per-provider, registering Gemini + Groq (+ Mistral) multiplies free capacity:
the AI layer's budget manager could route per stage with fallback-on-429 across providers.
**Not implemented at launch** (owner: Gemini only) — if quota pressure materialises (R-11), a
simple ordered-fallback list per stage is the first remedy to propose.
