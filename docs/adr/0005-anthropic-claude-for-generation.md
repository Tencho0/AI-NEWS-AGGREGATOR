# ADR-0005 — Anthropic Claude for analysis & generation; model-per-stage

**Status:** Rejected (owner decision 2026-07-02: free-tier provider required) — superseded by
[ADR-0010](0010-provider-agnostic-ai-gemini-default.md) · **Date:** 2026-07-02

## Context

The pipeline needs (a) high-volume cheap classification/summarisation, (b) top-quality Bulgarian
long-form drafting, (c) machine-readable outputs, (d) bounded cost. An official C# SDK exists for
Anthropic (`Anthropic` NuGet), matching our stack (ADR-0002).

## Options considered

1. **Anthropic Claude, model-per-stage** — `claude-opus-4-8` ($5/$25 per MTok) for drafting;
   `claude-haiku-4-5` ($1/$5) for volume analysis; Batch API (−50 %) for non-urgent stages;
   prompt caching for the stable style-guide prefix; structured outputs for JSON contracts.
2. Single model for everything — simpler, but either overpays (Opus for classification) or
   under-delivers (Haiku prose quality).
3. Other providers / local models — no evaluated advantage for Bulgarian long-form quality;
   local models add GPU/ops burden the VPS can't carry. Not pursued for v1.

## Decision

Option 1, behind an `IAiClient` abstraction with model + prompt version configured per stage, so
models can be swapped from config as the catalog evolves. Every call is metered into
`nw_CostLedger` with a hard daily cap.

## Consequences

Best quality where it matters, lowest cost where volume lives; a provider dependency mitigated by
the abstraction layer and versioned prompts; prompt/model changes are visible, reviewable events
(decision-log entries + golden-set eval per 08-testing.md).
