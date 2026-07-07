---
created: 2026-07-07T13:23:30.006Z
title: Review how TrendJob works
area: worker
files:
  - src/Newsroom.Worker/Jobs/TrendJob.cs
---

## Problem

We need to understand how TrendJob works — its trigger/schedule, what data it
reads, what it computes (trends/scoring), and what it produces downstream.
Currently the behavior isn't documented and needs a walkthrough.

## Solution

TBD — read through TrendJob.cs, trace its inputs and outputs, and document the
flow (when it runs, what it operates on, and where its results feed into).
