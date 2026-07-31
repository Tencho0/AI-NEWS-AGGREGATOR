# ADR-0013 — Burnt-in cover text, local logo compositing, persistent image storage, and code-based quota classification

**Status:** Accepted · **Date:** 2026-07-31 · **Builds on:** ADR-0012 (photoreal covers on FLUX.2
klein 4B), ADR-0009 (sourcing tiers)

## Context

ADR-0012 shipped photoreal covers but left three things unresolved and one thing wrong.

1. **Covers carried no text.** The intent was for the application to add the headline, key numbers
   and branding as layers afterwards — but that renderer was never built, so covers went out as
   bare photographs while the competition posts covers with a headline burnt in. FLUX.2 klein
   unifies generation and editing and renders text far better than FLUX.1 did, so the text can come
   from the same call that makes the image. What it cannot be trusted with is a *brand asset*: a
   diffusion model reproduces a wordmark approximately, and approximately is wrong for a logo.
2. **Quota classification was keyword-based and wrong.** Every HTTP 429 was read as "free
   allocation exhausted", which armed a lock that stopped cover generation until UTC midnight.
   Cloudflare returns 429 for *both* code 3036 (the daily allocation really is gone) and code 3040
   (capacity temporarily exceeded — a blip that clears in seconds). A capacity blip therefore cost
   a whole day of covers. The message-keyword fallback ("neurons", "quota", "billing") made it
   worse: any unrelated error whose text mentioned those words armed the same lock.
3. **Oversized reference portraits were discarded.** Cloudflare requires each `input_image_N` to be
   smaller than 512×512. A perfectly good mayor's portrait at 1200×900 was skipped, and the cover
   silently lost the likeness — for a resize.
4. **Images lived in the deployment directory.** `generated-images/` and `editor-uploads/` resolved
   against `AppContext.BaseDirectory`, and `nw_DraftImage.Url` stored the resulting **absolute
   path**. A redeploy, a service reinstall or a `bin` wipe destroyed every pending draft's cover,
   and moving the install directory orphaned rows permanently. Nothing ever deleted the files
   either, so the directory grew without bound.

## Decision

### Text is rendered by the model; the logo is not

The drafting model now returns a small cover-text plan alongside the scene:
`coverHeadline` (a 2–5 word Bulgarian headline, ≤ 42 chars), `coverKeyPoints` (0–3 figures or
highlights, ≤ 18 chars each), `coverTextEmphasis` (`headline` | `number` — the visual hierarchy)
and `coverTextPlacement` (`lower-third` | `lower-left` | `lower-right` | `left-third` |
`right-third` | `upper-left`). `CoverTextPlan` strips prompt-breaking characters — a stray `"`
would close the quoted string in the prompt — collapses whitespace, caps every length at a word
boundary, and drops the plan entirely when nothing renderable survives (the cover is then
text-free, exactly as before).

`FluxPromptComposer` passes those strings **verbatim in quotes** with explicit placement, size
hierarchy, typography, alignment, colour and contrast, and fences everything else out: no other
text of any kind, and no logo, wordmark, brand name or watermark anywhere — least of all in the
corner reserved for the real one. `ImageCompositor.OverlayLogo` then draws the genuine
`Images:Cover:LogoFile` PNG into `Images:Cover:LogoCorner` after generation, so the brand mark is
always pixel-exact. A missing or unreadable logo asset is a warning, never a lost cover.

Because every rendered glyph is unverified until a human looks at it — and Cyrillic is the hardest
case for any diffusion model — the generated cover continues to go through Telegram review before
publication. That review is now load-bearing, not just editorial preference.

**Prompt budget.** The typography block pushed the prompt past FLUX's 2048-character limit, and the
old code truncated the tail — which is where the "no other text / no logo" fences live. Cutting
them off is a correctness failure, not a cosmetic one. The composer now assembles all fixed parts
first and gives the scene (the only unbounded input) whatever budget remains, trimming it at a word
boundary or dropping it outright. The fences can no longer be truncated away.

### Quota classification reads the structured error code

`CloudflareAiException` carries two flags instead of one:

| Signal | Meaning | Behaviour |
|---|---|---|
| code **3036**, or HTTP **402** | daily free allocation gone / spend wall | lock generation until 00:00 UTC, alert the editor once on Telegram, stock covers meanwhile. Never retried. |
| code **3040**, or HTTP **503** | temporary capacity | bounded retry (`TransientRetries`, default 2, linear backoff), then stock for *this draft only*. **No lock.** |
| anything else | ordinary failure | stock for this draft, full retry next draft. **No lock.** |

The message-keyword heuristic is deleted. Words in an error string no longer stop the day's covers.

### Reference portraits are resized, not dropped

`ImageCompositor.ShrinkToReferenceLimit` downscales to fit under 512 px preserving aspect ratio
(long side pinned to 511), and never upscales — an already-small photo is passed through
un-re-encoded. Only a genuinely undecodable file still falls back to an anonymous cover.

### Images live outside the deployment directory

`Images:StorageRoot` is a persistent location — `%ProgramData%\PredelNewsroom\images` by default,
**a mounted volume in production** — holding three areas: `generated-images`, `editor-uploads` and
`public-figures`. `ImageStorage` is the single seam that maps between them and the database.

`nw_DraftImage.Url` now stores a **relative storage key** (`generated-images/flux-….jpg`), resolved
through the configured root on read by `ReviewRepository` and `PublishRepository`. Remote stock URLs
are untouched and still distinguished by `SourceKind`. Pre-ADR-0013 rows holding absolute paths
still resolve, but only when they land inside the storage root or the old deployment directory.

**Path traversal is refused by construction:** a key is combined with the root, normalized, and then
checked for containment; anything that escapes — `../`, a rooted path, a malformed value — resolves
to nothing at all. Callers degrade (publish without a cover, skip the dispatch) rather than reading
an arbitrary file. Area names are validated at startup, so a misconfigured `GeneratedImageDir`
fails loudly instead of writing outside the root.

### A daily retention pass

The existing daily `RetentionJob` gained an image pass with two independent guards. The SQL only
returns rows whose draft has **finished its life** — a draft awaiting approval, editing, scheduling
or publication keeps every image it has, selected or not — and the job then deletes a file only when
it resolves *inside* the generated-images or editor-uploads area, so a corrupted Url can never point
the deleter at a reference portrait, the logo, or anything outside the root.

| What | Age | Config |
|---|---|---|
| Generated covers on a discarded draft (Rejected/Superseded/Expired/GenerationFailed) | 14 days | `Retention:GeneratedImageDays` |
| Generated covers **not selected** on a published draft | 14 days | `Retention:GeneratedImageDays` |
| Editor uploads on a discarded draft | 30 days | `Retention:EditorUploadDays` |
| Any local image on a **Published** draft — Umbraco holds the durable copy | 30 days | `Retention:PublishedImageDays` |
| **Public-figure reference photos** | never — reusable inputs | — |
| Anything on an active or unpublished draft | never | — |

Migration 0014 adds `nw_DraftImage.FilePrunedAtUtc`. The row itself is kept as the audit trail of
what the editor saw; the stamp makes the pass idempotent so a deleted file never comes back as a
candidate. A locked file is left unstamped and retried tomorrow.

## Consequences

Covers now ship feed-ready with a headline and key figures, which is the single biggest lever on
click-through — but the pipeline's correctness now depends on a human checking the rendered Cyrillic
before publication. That is a deliberate trade: the alternative (a text-layer renderer with embedded
fonts) is more code, more assets and its own layout bugs, and it can wait until the model's text
quality is known from real output. If misspellings turn out to be frequent, the renderer becomes the
answer and the prompt drops back to text-free — the composer already supports that path, and it is
what happens automatically whenever the model returns no usable headline.

A capacity blip no longer costs a day of covers, and no error message can talk the worker into
stopping. The spend fence is unchanged and now narrower: only code 3036 and HTTP 402 stop
generation, and neither is ever retried.

**Deployment is no longer self-contained.** The worker install directory is disposable by design,
which is the point, but it means production *must* configure `Images:StorageRoot` on a persistent
mounted volume before drafts are generated — otherwise images land in `%ProgramData%` on whatever
host happens to run the service. This is documented in `docs/07-operations.md` and
`docs/05-integrations/images.md`.

SkiaSharp is a new dependency (native, MIT) for the two pixel operations. It is used nowhere else,
and both entry points degrade to a no-op on failure, so a broken native load costs the logo and the
resize — not the cover.
