# ADR-0012 — Photoreal cinematic covers on FLUX.2, and depicting public figures only from approved references

**Status:** Accepted · **Date:** 2026-07-31 · **Supersedes:** the cover *style* and *model* parts
of ADR-0011 (its generation-first-with-stock-fallback structure, metering and config layout stand)

## Context

ADR-0011 shipped generated covers and asked FLUX.1 Schnell for "premium magazine-cover editorial
illustration … clearly an illustration, not a photograph". Two problems showed up in the feed:

1. **The style underperforms.** The illustration directive plus a 4-step distilled model yields
   flat, poster-like frames with muted palettes. Against real press photos in a Facebook feed they
   read as filler and do not stop the thumb.
2. **The subject was a keyword bag.** The prompt joined the drafting model's English
   `imageSearchQueries` with semicolons. Three stock-search phrases are not a scene, so the model
   invented an incoherent composite instead of the one moment the article is about.

Separately, regional news is frequently *about a person* — a mayor signing a contract, a governor
opening a site. ADR-0009's "no real identifiable people" rule kept those covers generic, and the
obvious shortcut (name the person in the prompt) is exactly the thing that must never happen: a
diffusion model asked for a named person produces a confident, wrong face attached to a real
identity.

## Options considered

**Style / model**

1. **Keep FLUX.1 Schnell, drop the illustration directive.** Free, one-line change. But Schnell
   takes no reference image at all, so the public-figure half of the problem stays unsolvable, and
   4 fixed steps limit the realism ceiling.
2. **FLUX.2 [dev]** (`@cf/black-forest-labs/flux-2-dev`) — best realism, multi-reference. Billed in
   USD per tile per step (~$0.04 per 1280×720 at 25 steps). Rejected: real money per cover.
3. **FLUX.2 [klein] 9B** — reference support, but partner-priced in USD (~$0.015/cover). Rejected
   for the same reason.
4. **FLUX.2 [klein] 4B** (`@cf/black-forest-labs/flux-2-klein-4b`) — neuron-priced (5.37 neurons
   per input 512×512 tile, 26.05 per output tile), so a 1280×720 cover is ≈ 92 neurons and sits
   inside the Workers AI free daily allocation of 10,000 neurons (~100 covers/day of headroom).
   Unifies generation and editing, accepts up to four reference images. **Chosen.**

**Public figures**

5. **Never depict anyone.** Safe, but every person-driven story gets a generic cover.
6. **Name the figure in the prompt.** Rejected outright — a synthesised likeness from a name is a
   fabricated depiction of a real person.
7. **Reference-image-gated depiction.** A configured allow-list, each entry carrying an approved
   reference photo; the drafting model judges whether the person is genuinely central; the image
   layer refuses to depict anyone without a readable reference. **Chosen.**

## Decision

**Style.** Covers are cinematic photorealistic editorial news photographs, not illustrations.
`FluxPromptComposer` asks for vivid saturated colour and high contrast, one clear primary subject
just off-centre inside the central safe area with supporting elements, distinct foreground /
middle ground / background, real materials and correct anatomy, and the true local setting. It
explicitly rejects empty scenes, generic silhouettes, flat vector or poster styling, muted
palettes, posed studio arrangements and collage layouts.

**One scene, not keywords.** The drafting model returns a new field, `imageScene`: one coherent
English sentence naming the moment as it unfolds — subject, place (city, village, road, mountain,
forest, field, institution, sports venue…), time of day and weather, all taken from the article.
That is the prompt's subject. `imageSearchQueries` stays for the stock fallback and is used as the
scene only when `imageScene` is missing or came back in Bulgarian.

**Category is a tint, not a template.** The per-category line now sets mood, light and palette
only; the scene always comes from the article.

**Framing for later overlays.** 16:9 at 1280×720; subjects stay inside the central safe area; the
lower third and upper-right corner are asked to stay visually calm so the application can place
the headline, selected key numbers, article highlights and Predel News branding as separate layers
afterwards. The generator still produces a strictly text-free image — no text, letters, numbers,
logos or watermarks are ever generated. **The overlay renderer itself is not built in this
change**; the covers are prepared for it.

**Public figures.** `Images:PublicFigures` is a list of `{Name, Role, ReferenceImage, Aliases}`.
Only figures whose name or alias literally appears in the topic's sources are offered to the
drafting model, which returns `imageCentralPerson` — one of those names, or null. The image layer
then applies four independent gates before any likeness is drawn:

- the name must resolve to a configured figure (a hallucinated name is dropped);
- the story's category must not be sensitive — `Криминално` gets a symbolic, event-focused cover
  by default, overridable per deployment with
  `Images:Cloudflare:AllowPublicFiguresInSensitiveCategories`;
- the approved reference file must exist and be a PNG/JPEG under Cloudflare's 512 px reference
  limit (checked from the header, so an oversized photo is skipped rather than failing the cover);
- the wire format must be able to carry it (the legacy JSON path cannot).

When all pass, the photo rides along as `input_image_0` and the prompt says "the person in
reference image 1 is {Name}, {Role} — render their likeness from that reference only", forbids
inventing any action, location, gesture or circumstance beyond the scene, and forbids implying
guilt, arrest, detention, confrontation or misconduct. When any gate fails, the cover is anonymous
and everyone in frame is a non-identifiable fictional person. **A name alone never becomes a
likeness.**

**Cost.** The free allocation is a ceiling, never a starting point. HTTP 429/402 and spend-wall
wording ("neurons", "quota", "billing", "paid plan", …) are classified as
`CloudflareAiException.QuotaExhausted`; `FeaturedImageService` then alerts the editor on Telegram
once, stops calling the endpoint until UTC midnight, and serves stock covers in the meantime. It
never retries, never escalates to a billable model, and nothing in the pipeline can enable paid
billing.

**Attribution stays "Илюстрация"** (owner decision) even though the images are now photoreal.

Config added under `Images:Cloudflare:*`: `RequestFormat` (Multipart | Json), `Guidance`,
`ReferenceImageDir`, `AllowPublicFiguresInSensitiveCategories`; `Steps` now defaults to 0 (omitted,
because klein fixes steps at 4). Details in `docs/05-integrations/images.md`.

## Consequences

Covers look like reportage instead of stock illustration, which is the point — and that raises the
bar on labelling: a photoreal cover credited only "Илюстрация" leans harder on the credit line
than a visibly drawn one did, so the wording is worth revisiting if readers misread it.

Person-driven regional stories can finally show the person, but only after an editor has put an
approved photo on disk — the allow-list is deliberate operational work, and an empty list (the
default) means the feature is simply off. Reference photos must be under 512 px; oversized ones are
silently skipped with a warning rather than breaking the cover.

The model is a Cloudflare *partner* model. Cloudflare's pricing page lists it in neurons and does
not document any partner-model exclusion from the free allocation, so this rests on the neuron
pricing being honoured against the free tier; the quota guard above is what makes a wrong
assumption cheap — the worker stops and asks rather than spending.

The wire format changed from JSON to multipart, so `Images:Cloudflare:RequestFormat=Json` plus the
old model id is the one-step rollback to ADR-0011 behaviour.
