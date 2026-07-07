---
created: 2026-07-07T13:23:30.006Z
title: Clarify what and when we send to Google API
area: google-api
files:
  - src/Newsroom.Worker/Jobs/DraftJob.cs
---

## Problem

Unclear what content we send to the Google API and at what stage. Do we send
only the confirmed/"sure" articles, or every article that comes through? This
affects both cost and free-tier usage (see [[check-google-api-free-tier-limits]]).

## Solution

TBD — trace where the Google API is called (drafting path in DraftJob.cs and
any other callers) and document exactly which articles trigger a call and when.
