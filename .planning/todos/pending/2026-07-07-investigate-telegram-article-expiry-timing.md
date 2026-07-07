---
created: 2026-07-07T13:23:30.006Z
title: Investigate Telegram article expiry timing
area: telegram
files:
  - src/Newsroom.Worker/Jobs/TelegramJob.cs
---

## Problem

An article that was not answered in the Telegram review loop gets marked as
expired. We need to review the expiry window (the time before an article
expires) and understand why unanswered articles are being expired — is the
timeout too short, or is the expiry being triggered incorrectly?

## Solution

TBD — review the expiry/timeout logic in TelegramJob.cs (and wherever the
review-loop deadline is set) and confirm the intended window vs. actual
behavior.
