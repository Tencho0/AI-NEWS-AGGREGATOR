# ADR-0002 — .NET 10 worker service as the platform

**Status:** Proposed · **Date:** 2026-07-02

## Context

The publishing target (Predel-News) is Umbraco 17 on .NET 10, developed and operated by the same
developer, hosted on one Windows VPS with SQL Server Express and IIS. The aggregator needs
scheduled background jobs, HTTP scraping, AI/Telegram/Facebook API clients, and DB persistence.

## Options considered

1. **C# / .NET 10 worker service** — same language, runtime, hosting, and patterns (Dapper, xUnit)
   as the existing site; trivially shares the VPS as a Windows Service; one skillset to maintain.
   Con: scraping/NLP ecosystem is richer in Python.
2. **Python service** — best scraping/NLP libraries (trafilatura, feedparser). Cons: second
   runtime and deployment story on a Windows VPS, second skillset, weaker typing across a
   long-lived codebase, no code-sharing with the site.
3. **Node.js** — decent libraries, but same "second stack" cons and no advantage over C# here.

## Decision

Option 1 — .NET 10 worker service (`Newsroom.Core` / `Newsroom.Infrastructure` /
`Newsroom.Worker`), consistent with the existing solution's conventions. The needed libraries
exist and are mature (AngleSharp, Telegram.Bot, official Anthropic C# SDK, Dapper, Polly, Serilog).

## Consequences

One stack, one VPS, familiar patterns; scraping heuristics are hand-rolled rather than imported
from the Python ecosystem (acceptable — RSS-first strategy minimises HTML heuristics; see
scraping doc). If a JS-rendered source ever becomes essential, Playwright for .NET exists
(separate ADR before adopting).
