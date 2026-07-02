# 04 — Technical Specification

**Status:** Draft · **Last updated:** 2026-07-02

## Stack (ADR-0002, ADR-0003, ADR-0004)

| Concern | Choice | Notes |
|---|---|---|
| Language / runtime | C# / .NET 10 | Matches the Umbraco site; one skillset, one VPS |
| Host model | `BackgroundService`-based worker, installed as a **Windows Service** | `Microsoft.Extensions.Hosting.WindowsServices` |
| Scheduling | In-process `PeriodicTimer` loops per job; per-job enable flags + intervals from config | Upgrade path: Quartz.NET if cron-precision or clustering is ever needed (revisit end of Phase 3) |
| Database | **SQL Server Express** (existing instance), new database `Newsroom` | Never the Umbraco DB |
| Data access | Dapper + hand-written SQL migrations (versioned, applied at startup) | Mirrors Predel-News patterns |
| HTTP | `IHttpClientFactory` + Polly (retry w/ jitter, circuit breaker per host) | |
| HTML parsing | AngleSharp; RSS via `System.ServiceModel.Syndication` | Playwright only if a must-have source requires JS (own ADR first) |
| AI | Provider-agnostic via `Microsoft.Extensions.AI` (`IChatClient`); default provider **Gemini** (`Google.GenAI` NuGet, free tier) | Provider+model per stage from config — ADR-0010, see 05-integrations/ai-generation.md |
| Telegram | `Telegram.Bot` NuGet, long polling | ADR-0006 |
| Facebook | Raw Graph API via `HttpClient` (no maintained official .NET SDK) | ADR-0008 |
| Logging | Serilog → rolling file + SQL sink for warnings+ | See 07-operations.md |
| Tests | xUnit + NSubstitute + Verify (snapshot prompts/outputs) | See 08-testing.md |

## Data model (database `Newsroom`)

All tables prefixed `nw_`. Times in UTC (`datetime2`). Soft status columns, no hard deletes for
pipeline entities (audit).

| Table | Purpose / key columns |
|---|---|
| `nw_Source` | Configured sources: `Id, Name, Kind(rss/sitemap/html), Url, ParserHint, IntervalMinutes, Enabled, LastCrawledAt, LastError` |
| `nw_SourceArticle` | Scraped items: `Id, SourceId, Url(canonical, unique), Title, Author, PublishedAt, ExtractedText, ContentHash, Status(New/Analysed/Ignored), FirstSeenAt` |
| `nw_ArticleAnalysis` | 1:1 with SourceArticle: `Summary, Category, RegionScore, EntitiesJson, ModelUsed, TokensIn/Out, Cost` |
| `nw_Topic` | Clusters: `Id, Label, Status(Emerging/Hot/Muted/Done), Score, FirstSeenAt, LastScoredAt, MutedUntil` |
| `nw_TopicArticle` | N:M topic ↔ source articles, with similarity score |
| `nw_Draft` | `Id, TopicId, Version, Status(Generating/PendingReview/Approved/Rejected/Expired/Publishing/Published/PartiallyPublished/PublishFailed/GenerationFailed), Headline, Subtitle, BodyMarkdown, Category, Region, TagsJson, SeoTitle, SeoDescription, SourcesJson, PromptVersion, ModelUsed, TokensIn/Out, Cost, CreatedAt` |
| `nw_DraftImage` | Suggestions per draft: `DraftId, Ordinal, SourceKind(stock/library/ai/editor-upload), Url/LocalPath, Attribution, AltTextBg, Selected(bit)` |
| `nw_ReviewAction` | Audit of editor actions: `DraftId, TelegramUserId, Action, Comment, At` |
| `nw_PublishRecord` | Per destination: `DraftId, Destination(umbraco/facebook), ExternalId, ExternalUrl, Status, Error, At` |
| `nw_AuditEvent` | Generic state-transition log: `Entity, EntityId, FromStatus, ToStatus, Detail, At` |
| `nw_CostLedger` | Every AI call: `Provider, Stage, Model, TokensIn, TokensOut, RequestCount, Cost, At` — doubles as the free-tier **request-quota ledger** (daily RPD budget checks + reporting) |
| `nw_Config` | Key/value runtime configuration with `UpdatedAt` |
| `nw_TelegramState` | Long-poll offset, pending conversations (e.g. awaiting change-instructions for draft X) |

Retention: `ExtractedText` and raw analysis payloads pruned after 90 days (configurable);
metadata rows kept indefinitely.

## Configuration & secrets

- Non-secret runtime config → `nw_Config` (+ `appsettings.json` defaults).
- Secrets (Gemini/AI-provider keys, Telegram token, FB token, Umbraco publishing credential, stock-API keys)
  → **environment variables of the Windows Service** (or `appsettings.Production.json` outside the
  repo with restricted ACLs). Never in git. See 06-security.md.

## External service accounts needed (provisioning checklist)

| Service | What's needed | Owner action |
|---|---|---|
| Google AI (Gemini) | ✅ provisioned 2026-07-02 (dev: `dotnet user-secrets`, see 06-security.md; VPS: env var at deploy). Check live rate limits in the AI Studio dashboard | Done (rotate key when convenient) |
| (optional, later) other AI provider | API key + billing, only if a stage is switched per ADR-0010 | Owner decision first |
| Telegram | Bot via @BotFather; editorial group chat id | Editor + dev |
| Facebook | Meta app, Page access token (`pages_manage_posts`), business verification, app review | Page admin — start early, review takes time (risk R-2) |
| Umbraco | API credential for the publishing endpoint | Dev (see 05-integrations/umbraco.md) |
| Stock images | Pexels and/or Pixabay API key (free tiers) | Dev |
