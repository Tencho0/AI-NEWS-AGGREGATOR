# Runbook — Add a Public Figure

**Status:** Agreed · **Last updated:** 2026-07-31 · **ADR:** 0012, 0013

Putting a real person's likeness on a generated cover takes two things: an **approved reference
photo** on disk, and an **allow-list entry** in `Images:PublicFigures`. Both are deliberate editorial
acts — the list ships empty and the feature stays off until someone populates it. Background and the
gates a cover must pass are in [05-integrations/images.md](../05-integrations/images.md).

Adding a figure is config + a file. It needs a worker restart, not a redeploy.

## 1. Decide the person belongs on the list

The allow-list answers "whose face may this system draw?", so it is an editorial decision, not a
technical one. Add people the outlet covers in their public role. Remember the downstream rule:
`Криминално` stories get a symbolic cover regardless, unless
`Images:Cloudflare:AllowPublicFiguresInSensitiveCategories` is turned on.

## 2. Get a portrait — and its licence

Acceptable sources, in order of convenience:

1. **Wikidata `P18` → Wikimedia Commons.** Look up the person on wikidata.org, open the `image`
   claim, take the file from Commons. Note the **licence** (usually a CC BY variant) and the
   **author** from the file page.
2. **Official institutional portrait** — ministry, parliament, municipality press page.
3. **A photo the outlet owns.**

Never a photo lifted from a scraped article (see the hard rule in images.md).

**Write the licence and author down** — in the commit message for the config change, or a credits
file kept with the images. CC BY obliges attribution wherever the derived cover is published, and
the JPEG on disk carries no record of it.

### Bulk-fetching from Wikimedia

If you are onboarding many people at once, expect the anonymous rate limit. It is **per IP across
all Wikimedia hosts** — `wikidata.org`, `commons.wikimedia.org`, `query.wikidata.org` and
`upload.wikimedia.org` share one budget — and it answers `429` with a `Retry-After` hint that
**grows** if you retry inside the penalty window. A naive one-request-per-person loop exhausts the
budget in seconds and then fails everything.

What works: resolve names in bulk with **one SPARQL query** per ~20 names against
`query.wikidata.org` (`rdfs:label` + `wdt:P31 wd:Q5`, with `wdt:P18`), batch Commons metadata
**50 titles per `imageinfo` call**, **cache the resolved URLs to disk**, and only then download the
files — pacing them and honouring `Retry-After`. Caching matters most: a resumed run that re-resolves
everything spends its whole budget before the first image. Alternatively, save the files from a
browser, which is not affected by the API limiter.

## 3. Verify the face

Confirm the photo is the right person before it goes in. Bulgarian names collide badly and
prominence misleads — sorting Wikidata matches by sitelink count returns the footballer, not the
minister. Check the Wikidata description and the person's office, not just the name.

If you cannot disambiguate with confidence, **leave them off**. An anonymous cover is a non-event;
the wrong person's face on a news cover is a correction, or worse.

## 4. Name the file and drop it in

The file name is referenced verbatim by `ReferenceImage`, so pick it deliberately. Convention in use
is `Име_Фамилия.jpg` — Cyrillic is fine, `ImageStorage` combines the key with the root and
containment-checks the result.

```powershell
Copy-Item "<portrait>" (Join-Path $env:ProgramData "PredelNewsroom\images\public-figures\Име_Фамилия.jpg")
```

PNG or JPEG. Any size — oversized portraits are downscaled to a long side of 511 at generation time;
small ones are never upscaled, so avoid anything tiny or heavily compressed.

## 5. Add the allow-list entry

In `appsettings.json` (or the environment's override):

```jsonc
"Images": {
  "PublicFigures": [
    {
      "Name": "Име Фамилия",              // what the drafting model must echo back
      "Role": "кмет на Благоевград",       // used verbatim in the image prompt
      "ReferenceImage": "Име_Фамилия.jpg", // inside ReferenceImageDir
      "Aliases": ["кметът Фамилия"]        // extra spellings the sources use
    }
  ]
}
```

- **`Role` goes into the prompt word for word.** "кмет на Благоевград" produces a better cover than
  a generic "български политик" — write the office, not a category.
- **`Aliases`** should cover how sources actually write the name. Matching is whole-token, so
  „Иванов" does not fire on „Иванова".
- An entry without a `Name` or a `ReferenceImage` is silently dropped at startup.

## 6. Verify after adding

1. Restart the worker; a missing or unreadable file logs a warning at first use and the cover falls
   back to the anonymous path.
2. Wait for a draft whose sources mention the person, or generate one. The cover only shows a face
   when every gate passes: mention → `imageCentralPerson` centrality → known name → category →
   decodable reference photo → multipart FLUX.2 wire format.
3. Check the Telegram review card. **Review is the real check** — an editor confirms the likeness
   and any burnt-in text before publication.

## Current state (2026-07-31)

`public-figures/` holds 50 curated portraits; `Images:PublicFigures` is still `[]`, so no face is
drawn yet. Licence and author were not recorded for that initial batch — recover them from each
Commons file page before those covers are published, and treat step 2 as mandatory from here on.
