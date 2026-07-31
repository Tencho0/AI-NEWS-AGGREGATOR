# Integration — Image Sourcing & Suggestion

**Status:** Draft · **Last updated:** 2026-07-31 · **ADR:** 0009, 0011, 0012, 0013

## Hard rule

**Never reuse images from scraped articles.** Press photos are licensed to their publishers;
republishing them is a copyright violation with real financial risk (see risk R-4, 06-security.md).

## Sourcing priority (ADR-0009, ADR-0011)

Implemented automated flow: for every automated draft, `FeaturedImageService` **generates an AI
illustration first** (ADR-0009's tier 3, implemented via Cloudflare Workers AI FLUX.1 Schnell —
ADR-0011); on any failure it falls back automatically to the free stock APIs.

| # | Source | When | Attribution |
|---|---|---|---|
| 1 | **AI-generated cover** — Cloudflare Workers AI, FLUX.2 klein 4B (ADR-0012); cinematic photoreal editorial artwork; text-free; real people only from an approved reference photo | Tried first for every automated draft | caption "Илюстрация" |
| 2 | **Free stock APIs** — Pexels, Pixabay (free licences, API access) | Automatic fallback when generation is unconfigured, over budget, or fails | per licence; stored in `nw_DraftImage.Attribution` and shown in caption/credit |
| 3 | **Own media library** (existing site media, tagged by topic: town views, institutions, recurring subjects) | Aspirational — ADR-0009's tier 1, **not implemented** | none needed |
| 4 | **Editor upload** via Telegram reply | Editor has a real photo (own/press-release material) | editor's responsibility |

The drafting model outputs one English `imageScene` (the cover's subject) plus 2–3 English
`imageSearchQueries` (the stock fallback's queries); every candidate carries attribution + AI-written
Bulgarian alt text (the site validates cover-image alt text). When AI generation succeeds it is the
single suggestion in the Telegram review card (no stock cycling); when it fails the editor sees up
to 3 stock candidates as before. The editor can always reject, regenerate, or attach their own photo.

## Rules

- Real, identifiable people: own-library, editor-supplied, or — since ADR-0012 — a configured
  public figure generated **only** from their approved reference photo (see below). Never from a
  name alone.
- Every image stored with: source kind, origin URL/id, licence string, attribution, alt text.
- The selected image is uploaded to Umbraco as a Media item by the publishing endpoint;
  Facebook uses the article's OG image (no separate re-hosting in v1).
- AI image generation provider: **Q-4 resolved by ADR-0011, restyled and re-modelled by ADR-0012**
  — Cloudflare Workers AI, FLUX.2 klein 4B (`@cf/black-forest-labs/flux-2-klein-4b`,
  multipart/form-data, neuron-priced inside the free daily allocation ≈ 92 neurons per 1280×720
  cover). Prompt (`FluxPromptComposer`) is composed from the article's own details only —
  `imageScene` + region + headline-as-context — with hard no-text and never-lurid directives;
  attribution "Илюстрация"; default 1280×720 (16:9, ≥ the site's 1,200 px Discover minimum); file
  saved on the worker's disk (`Images:Cloudflare:GeneratedImageDir`), row stored with
  `SourceKind='ai'`; metered under budget stage "Image"
  (`Ai:Stages:Image:DailyRequestBudget`, cost 0 inside the free allocation). Config:
  `Images:Cloudflare:{AccountId, ApiToken, Model, RequestFormat, Steps, Guidance, Width, Height,
  GeneratedImageDir, ReferenceImageDir, AllowPublicFiguresInSensitiveCategories}` — secrets per
  06-security.md, never in git. `RequestFormat=Json` + the FLUX.1 Schnell model id is the rollback
  to pre-ADR-0012 behaviour.

## Cover style (ADR-0012)

Cinematic photoreal editorial news artwork — deliberately *not* illustration:

- one coherent scene from the article (`imageScene`), showing a moment actively unfolding;
- one clear primary subject just off-centre, supporting elements from the same event, distinct
  foreground / middle ground / background;
- the story's real setting (city, village, road, mountain, forest, field, institution, sports
  venue) with southwest-Bulgarian architecture and terrain when relevant; time of day, weather and
  atmosphere come from the article;
- category tints mood and palette only — it is never a scene template;
- vivid, saturated, high-contrast, arresting in a feed; realistic materials, anatomy, clothing,
  vehicles and buildings;
- 16:9, subjects inside the central safe area, with the text area and the logo corner kept visually
  calm;
- never: blood or gore, empty scenes, generic silhouettes, flat vector or poster styling, muted
  palettes, posed studio arrangements, collage layouts.

## Cover text and logo (ADR-0013)

FLUX.2 renders the headline and key figures **into** the image; the logo is composited afterwards
from the real asset.

The drafting model returns a cover-text plan next to the scene:

| Field | Contract |
|---|---|
| `coverHeadline` | 2–5 word Bulgarian headline, ≤ 42 chars, no full stop, no quotes — read at a glance, not a repeat of the article headline |
| `coverKeyPoints` | 0–3 very short figures/highlights, ≤ 18 chars each (`"3 сгради"`, `"12 млн. лв."`). Empty when the story has no strong number. No sentences. |
| `coverTextEmphasis` | `headline` (headline dominates) or `number` (the news *is* the figure) — the visual hierarchy |
| `coverTextPlacement` | `lower-third` · `lower-left` · `lower-right` · `left-third` · `right-third` · `upper-left` — must not cover the scene's primary subject |

`CoverTextPlan` cleans and caps all of it: prompt-breaking characters are stripped (a stray `"`
would close the quoted string in the prompt), whitespace collapses, lengths truncate at a word
boundary, blanks and duplicates drop. If nothing renderable survives, the cover is **text-free** —
the pre-ADR-0013 behaviour, reached automatically rather than by a switch.

`FluxPromptComposer` then passes the strings **verbatim in quotes** with explicit placement, size
hierarchy, typography (heavy condensed sans, Bulgarian Cyrillic letterforms), alignment, colour and
contrast (white type over a soft dark scrim), and fences out everything else: no captions, sentences,
dates or source names, and **no logo, wordmark, brand name or watermark anywhere**.

The prompt is assembled fixed-parts-first, with the scene given whatever of the 2048-character
budget remains. This matters: the old blind tail-truncation could cut the text fences off the end.

**The logo is never generated.** `ImageCompositor` draws `Images:Cover:LogoFile` into
`Images:Cover:LogoCorner` (default upper-right, `LogoWidthPercent` / `LogoMarginPercent` for size and
inset) after generation, so the brand mark is pixel-exact. A missing or unreadable logo asset is a
warning, not a lost cover.

> **Review is load-bearing.** Every letter and number in the image is unverified until an editor sees
> it, and Cyrillic is the hardest case for any diffusion model. The generated cover always goes
> through the Telegram review card before publication — that check is what makes burnt-in text safe,
> not an editorial nicety.

## Public figures (ADR-0012)

`Images:PublicFigures` is the allow-list of people whose likeness a cover may show. Empty by
default — the feature is off until an editor populates it.

```jsonc
"Images": {
  "Cloudflare": { "ReferenceImageDir": "public-figures" },
  "PublicFigures": [
    {
      "Name": "Иван Иванов",                  // what the drafting model must echo back
      "Role": "кмет на Благоевград",           // used verbatim in the image prompt
      "ReferenceImage": "ivanov.png",          // inside ReferenceImageDir, PNG/JPEG under 512×512
      "Aliases": ["кметът Иванов"]             // extra spellings the sources use
    }
  ]
}
```

Flow, with every gate that must pass before a face is drawn:

1. **Mention** — only figures whose name or alias literally appears in the topic's sources become
   candidates (whole-token match, so „Иванов" does not fire on „Иванова").
2. **Centrality** — the drafting model returns `imageCentralPerson`: one candidate name, or null.
   It is told to pick a name only when the decision, statement, appointment, appearance, campaign
   or action is *theirs* — not when they are quoted in passing or named as background — and to
   return null on criminal, scandalous or accusatory stories.
3. **Known name** — the returned name must resolve to a configured figure; a hallucinated one is
   logged and dropped.
4. **Category** — `Криминално` gets a symbolic, event-focused cover instead of a face, unless
   `Images:Cloudflare:AllowPublicFiguresInSensitiveCategories` is turned on for the rare story
   where the figure is unquestionably the event.
5. **Reference photo** — the file must exist and be decodable. Cloudflare requires each
   `input_image_N` to be **under 512×512**, so an oversized portrait is **downscaled**
   (aspect-preserving, long side pinned to 511, never upscaled) rather than dropped — losing a
   likeness to a resize was a bug, not a policy. Only a genuinely undecodable file falls back to an
   anonymous cover.
6. **Wire format** — only the multipart FLUX.2 path can carry an image; the legacy JSON path never
   depicts anyone.

When all pass, the photo is sent as `input_image_0` and the prompt binds the model hard: render the
likeness *from that reference only*, present in the described scene, plausible everyday clothing and
neutral professional expression; invent no action, location, gesture or circumstance beyond the
scene; imply no guilt, arrest, detention, confrontation or misconduct. Everyone else in frame is an
ordinary fictional person with a non-identifiable face — which is also the whole-frame rule whenever
any gate fails.

## Reference portraits — where they come from (ADR-0012)

The allow-list is only half the gate; the photo behind each entry is the other half. Step-by-step
onboarding lives in [runbooks/add-a-public-figure.md](../runbooks/add-a-public-figure.md). The rules
that constrain it:

- **Never a scraped press photo** — the hard rule at the top of this document applies here most
  sharply, because a reference portrait is reused on every cover that person appears on. In practice
  the workable sources are Wikidata `P18` → Wikimedia Commons, an official institutional portrait,
  or a photo the outlet owns.
- **The licence travels with the file.** Commons portraits are mostly CC BY variants, which oblige
  us to attribute the photographer wherever the derived cover is published. Record licence and
  author per file when you add it — the file itself carries no such metadata, and a portrait whose
  provenance nobody wrote down cannot be defended later.
- **Verify the face, not just the name.** Bulgarian names collide hard, and prominence is a bad
  proxy for relevance: ranking Wikidata matches by sitelink count picks the footballer over the
  minister (Костадин Костадинов, Петър Витанов, Георги Пеев all resolve that way), and one name
  matched 38 distinct people. A wrong face on a cover is worse than no face — when a name cannot be
  disambiguated with confidence, leave it off the allow-list.
- **Size is not your problem.** Anything above 512×512 is downscaled at generation time
  (aspect-preserving, long side 511). Do not pre-shrink, and never upscale a small portrait.
- **A figure with no photo can never be depicted.** `ReadPublicFigures` drops entries without a
  `ReferenceImage`, so an unphotographed figure simply falls through to the anonymous-cover path
  rather than risking a name-only likeness.

## Cost safety and failure classification (ADR-0012, ADR-0013)

The Workers AI free daily allocation is a ceiling, not a starting point. Failures are classified by
Cloudflare's **structured error code**, never by words in the message — HTTP 429 alone means nothing,
because it carries both the real allocation wall and a transient capacity blip:

| Signal | Meaning | Behaviour |
|---|---|---|
| code **3036**, or HTTP **402** | daily free allocation gone / spend wall | alert the editor once on Telegram („⚠️ Безплатната дневна квота … Не включвам платен план"), stop calling the endpoint until 00:00 UTC, stock covers meanwhile. **Never retried.** |
| code **3040**, or HTTP **503** | capacity temporarily exceeded | bounded retry (`Images:Cloudflare:TransientRetries`, default 2, linear backoff via `TransientRetryDelaySeconds`), then stock for **this draft only**. No daily lock. |
| anything else | ordinary failure (bad prompt, auth, malformed payload) | stock for this draft, full retry on the next one. No daily lock. |

Nothing in the pipeline escalates to a billable model or enables paid billing.

## Where the files live (ADR-0013)

`Images:StorageRoot` is a **persistent location outside the worker's deployment directory** —
`%ProgramData%\PredelNewsroom\images` by default. It holds three managed areas plus the branding
asset:

| Area | Config | Contents | Retention |
|---|---|---|---|
| `generated-images` | `Images:Cloudflare:GeneratedImageDir` | AI covers | pruned, see below |
| `editor-uploads` | `Images:EditorUploadDir` | Telegram photo replies | pruned, see below |
| `public-figures` | `Images:Cloudflare:ReferenceImageDir` | approved reference portraits | **never pruned** |
| `branding` | the folder half of `Images:Cover:LogoFile` | the logo composited onto every cover | **never pruned** |

The default layout on a Windows box, all of it outside the repo and outside the install directory:

```text
%ProgramData%\PredelNewsroom\images\
├── generated-images\                 AI covers (pruned)
├── editor-uploads\                   Telegram photo replies (pruned)
├── public-figures\                   approved reference portraits, e.g. Румен_Радев.jpg
│                                     — file names are what Images:PublicFigures[].ReferenceImage
│                                       references verbatim; Cyrillic is fine
└── branding\
    └── predel-news-logo.png          Images:Cover:LogoFile — transparent PNG
```

Only the **three areas** are named settings validated at startup. The logo is an ordinary storage
key: `ImageStorage.TryResolve` accepts any relative path that stays inside the root, so
`branding/predel-news-logo.png` needs no area of its own. Point `Images:Cover:LogoFile` anywhere
inside the root and it resolves; point it outside and it is refused like a traversal attempt, and
the cover is composited without a logo (a warning, never a lost cover).

**The logo must have an alpha channel.** `ImageCompositor.OverlayLogo` draws it with SkiaSharp
straight onto the cover, so an opaque JPEG paints a solid rectangle in the corner. Crop the
transparent padding too: `LogoWidthPercent` sizes the *whole image*, so a mark that fills only part
of its canvas renders proportionally smaller than the number suggests.

> **Production requires a persistent mounted volume for `Images:StorageRoot`, and the worker install
> directory must stay disposable.** A redeploy, service reinstall or `bin` wipe must never take
> pending drafts' covers with it. Configure the volume *before* generating drafts — otherwise images
> land in `%ProgramData%` on whichever host runs the service.

`nw_DraftImage.Url` stores a **relative storage key** (`generated-images/flux-….jpg`), resolved
through the configured root by `ReviewRepository` and `PublishRepository`. Remote stock URLs are
unchanged and distinguished by `SourceKind`. Rows written before ADR-0013 hold absolute paths and
still resolve, but only inside the storage root or the old deployment directory.

**Path traversal is refused by construction** (`ImageStorage`): a key is combined with the root,
normalized, then checked for containment. `../`, rooted values and malformed paths resolve to
nothing, and callers degrade (publish without a cover, skip the dispatch) rather than read an
arbitrary file. Area names are validated at startup.

## Image retention (ADR-0013)

The daily `RetentionJob` deletes local image files that are no longer useful. Two independent
guards: the query only returns rows whose **draft has finished its life**, and the job only deletes
files that resolve *inside* the generated-images or editor-uploads area.

| What | Age | Config |
|---|---|---|
| Generated covers on a discarded draft (Rejected/Superseded/Expired/GenerationFailed) | 14 d | `Retention:GeneratedImageDays` |
| Generated covers **not selected** on a published draft (the unused versions) | 14 d | `Retention:GeneratedImageDays` |
| Editor uploads on a discarded draft (never approved or published) | 30 d | `Retention:EditorUploadDays` |
| Any local image on a **Published** draft — Umbraco holds the durable copy | 30 d | `Retention:PublishedImageDays` |
| **Public-figure reference photos** and the logo asset | **never** — reusable inputs | — |
| Anything on a draft still awaiting approval, editing, scheduling or publication | **never** | — |

`nw_DraftImage.FilePrunedAtUtc` (migration 0014) marks a row whose file is gone. The row is kept as
the audit trail of what the editor saw; the stamp makes the pass idempotent. A locked file is left
unstamped and retried tomorrow. Batch size: `Retention:ImageBatch` (default 500).
