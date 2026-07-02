# Integration — Umbraco Website Publishing

**Status:** Draft · **Last updated:** 2026-07-02 · **ADR:** 0007

## Context (facts from the Predel-News codebase, verified 2026-07-02)

- Umbraco **17.2.1** on .NET 10; content is invariant **Bulgarian**; SQL Server Express; IIS.
- `article` document type with: `headline`*, `subtitle`, `slug`*, `body`* (rich text),
  `coverImage`* (MediaPicker3), `category`* / `region` / `tags` / `author`* (custom pickers to
  taxonomy nodes), `publishDate`*, `isBreakingNews`, SEO composition (`seoTitle`,
  `seoDescription`, `ogImage`). Articles live under `newsRoot`.
- Existing patterns to reuse: `ManagementApiControllerBase` controllers in
  `PredelNews.BackofficeExtensions`, `ISlugGenerator` (Cyrillic→Latin), `IContentPublishingService`,
  `IMediaService`; **Delivery API is not enabled**; no public write API exists today.

## Decision (ADR-0007): a dedicated Publishing endpoint inside the site

Add one controller to the **Predel-News** solution (its repo, its release cycle):

```
POST /umbraco/management/api/v1/predelnews/publishing/articles
Authorization: Umbraco API user (client credentials, dedicated "newsroom-bot" user
               with permissions limited to News section)
```

Request (multipart or JSON + image URL fetched server-side — final shape decided in Phase 5):

```json
{
  "headline": "…", "subtitle": "…", "bodyMarkdown": "…",
  "categoryAlias": "…", "regionAlias": "…", "tags": ["…"],
  "authorKey": "…(default bot/staff author)…",
  "seoTitle": "…", "seoDescription": "…",
  "publishDate": "2026-07-02T10:00:00Z",
  "image": { "fileName": "…", "bytesBase64|sourceUrl": "…", "altText": "…", "attribution": "…" },
  "externalRef": "newsroom-draft-123"   // idempotency key
}
```

The endpoint (server-side, inside the site's invariants):
1. Idempotency check by `externalRef` (re-post returns the existing result).
2. Creates the Media item (folder `News/Automated/{yyyy}/{MM}`), validates alt text.
3. Maps markdown → the site's rich-text format; resolves category/region/tag/author pickers.
4. Generates slug via `ISlugGenerator`; creates content under `newsRoot`;
   sets `articleStatus`, publish date; **publishes** via `IContentPublishingService`.
5. Returns `{ contentKey, url }`.

Response URL is stored in `nw_PublishRecord` and used for the Facebook post.

### Why not the alternatives

- *Generic Umbraco Management API from outside* — possible (Umbraco 14+ API users), but the
  caller would have to know media formats, picker JSON shapes, slug rules → brittle coupling.
- *Direct DB writes* — rejected outright (bypasses Umbraco cache/index/invariants).
- *Auto-drafts inside Umbraco with backoffice approval* — duplicates the Telegram approval gate.

## Auth & hardening

- Dedicated Umbraco **API user** (client credentials) scoped to content section; secret stored as
  a worker secret. Endpoint additionally validates payload sizes, image type/size limits, and
  logs every call. Rate limit: publishing volume is tiny; a simple per-minute cap guards misuse.

## Failure behaviour

- Worker retries transient failures (3×, backoff). Idempotency key guarantees no duplicate
  articles. Persistent failure → `PublishFailed` + Telegram alert; the editor can retry from the
  message once the site is healthy.

## Change management

The request/response contract above is owned by **this** repo's docs; any change requires an ADR
here + a matching PR in Predel-News. Contract tests (08-testing.md) pin the shape on both sides.
