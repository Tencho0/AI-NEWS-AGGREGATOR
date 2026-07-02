# 03 — System Architecture

**Status:** Draft · **Last updated:** 2026-07-02
**Key ADRs:** 0002 (stack), 0003 (storage), 0004 (scheduling), 0007 (Umbraco publishing)

## Big picture

One new .NET 10 **worker service** ("the Newsroom worker") runs on the same Windows VPS as the
Umbraco site, as a Windows Service. It owns the whole pipeline and talks to four external systems.
One small **companion controller** is added inside the existing Umbraco solution to receive
publishes.

```
                        ┌────────────────────────── Windows VPS ─────────────────────────┐
                        │                                                                │
 News sources (RSS/HTML)│  ┌──────────────────────────────┐      ┌────────────────────┐  │
 ───────HTTP GET───────▶│  │  PredelNews.Newsroom.Worker  │      │  Predel-News site  │  │
                        │  │  (Windows Service, .NET 10)  │      │  (Umbraco 17, IIS) │  │
 AI provider (Gemini,   │  │                              │      │                    │  │
 ◀─pluggable)─HTTPS────▶│  │  Scheduler (hosted services) │      │ + PublishingApi-   │  │
                        │  │  ├─ ScrapeJob                │─────▶│   Controller (new, │  │
 Telegram Bot API       │  │  ├─ AnalyseJob               │HTTPS │   authenticated)   │  │
 ◀──────HTTPS──────────▶│  │  ├─ TrendJob                 │      └─────────┬──────────┘  │
   (long polling)       │  │  ├─ DraftJob                 │                │             │
                        │  │  ├─ TelegramUpdateLoop       │      ┌─────────▼──────────┐  │
 Facebook Graph API     │  │  └─ PublishJob               │      │ SQL Server Express │  │
 ◀──────HTTPS──────────▶│  │                              │      │ ├─ PredelNews (db) │  │
                        │  └──────────────┬───────────────┘      │ └─ Newsroom (db)   │  │
                        │                 └───────Dapper─────────▶ (separate database) │  │
                        │                                        └────────────────────┘  │
                        └────────────────────────────────────────────────────────────────┘
```

Design principles:
- **One deployable, many small jobs.** Each pipeline stage is an isolated hosted service reading
  and writing DB state; the database is the queue and the source of truth. Any job can crash and
  restart without losing work (statuses + retries live in the DB).
- **The Umbraco site is never touched directly** — no shared DB writes, no `IContentService`
  from outside. Publishing goes through one authenticated HTTP endpoint owned by the site
  (ADR-0007), so the site keeps control of its own content invariants (slugs, media, taxonomy).
- **All external calls are behind interfaces** (`IScraper`, `IAiClient`, `ITelegramGateway`,
  `IFacebookPublisher`, `IUmbracoPublisher`) so every stage is testable with fakes.

## Module breakdown

| Module (project) | Responsibility |
|---|---|
| `Newsroom.Core` | Domain model (SourceArticle, Topic, Draft, PublishRecord), state machine, interfaces, trend scoring, prompt templates. No I/O. |
| `Newsroom.Infrastructure` | Dapper repositories + migrations, HTTP scrapers (RSS/HTML adapters), Anthropic client wrapper, Telegram gateway, Facebook client, Umbraco publishing client. |
| `Newsroom.Worker` | Host: DI wiring, configuration, hosted services (jobs), health checks, logging setup. |
| `Newsroom.Core.Tests` / `Newsroom.Infrastructure.Tests` | Unit + integration tests. |
| *(in Predel-News repo)* `PublishingApiController` | Receives article + image, creates Media + Content, publishes, returns URL. |

## Data flow (per stage)

1. `ScrapeJob` → fetch feeds → upsert `SourceArticle(status=New)`.
2. `AnalyseJob` → batch `New` articles → AI summarise/classify → `Analysed` (or `Ignored`).
3. `TrendJob` → cluster `Analysed` window → upsert `Topic`; threshold → `Topic(status=Hot)`.
4. `DraftJob` → for `Hot` topics without an active draft → AI generate + image search →
   `Draft(status=PendingReview)` → hand to Telegram gateway.
5. `TelegramUpdateLoop` → long-poll updates → map button presses / replies to state transitions.
6. `PublishJob` → for `Approved` drafts → Umbraco publish → Facebook publish → `Published`
   (+ `PublishRecord` per destination) → Telegram confirmation.

Every transition writes an `AuditEvent` row (who/what/when/from→to).

## Recommended repository structure (this repo)

```
AI-NEWS-AGGREGATOR/
├── docs/                          # this documentation tree (see docs/README.md)
├── src/
│   ├── Newsroom.Core/
│   ├── Newsroom.Infrastructure/
│   ├── Newsroom.Worker/
│   └── tests/
│       ├── Newsroom.Core.Tests/
│       └── Newsroom.Infrastructure.Tests/
├── tools/                         # one-off scripts (backfill, source testing)
├── Newsroom.slnx
├── .editorconfig
├── .gitignore
└── README.md                      # points to docs/
```

The Umbraco-side `PublishingApiController` lives in the **Predel-News repo**
(`src/BackofficeExtensions/`), following that repo's existing controller patterns; its contract is
specified here in [05-integrations/umbraco.md](05-integrations/umbraco.md) and any change to the
contract requires an ADR in *this* repo.
