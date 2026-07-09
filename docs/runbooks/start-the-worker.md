# Runbook — Start the Worker (dev machine)

**Status:** Agreed · **Last updated:** 2026-07-09

How to start / stop / check the Newsroom worker **on this dev machine, by yourself** — no Claude
session needed. (For the VPS/production install see [deploy.md](deploy.md) and
[restore-after-vps-restart.md](restore-after-vps-restart.md).)

> **Why it keeps stopping:** if the worker is started from inside a Claude Code chat, it dies when
> that chat/session closes. Start it with **Option B (detached)** below and it survives closing the
> terminal — it runs until you stop it or the PC restarts.

## 0. Prerequisites (one-time / rarely change)

- **SQL Server must be running.** Check, and start if needed (PowerShell as admin):
  ```powershell
  Get-Service MSSQLSERVER            # Status should be "Running"
  Start-Service MSSQLSERVER          # only if it is stopped
  ```
- **Secrets are already set** in `dotnet user-secrets` for `Newsroom.Worker` (Gemini, Telegram,
  Facebook). You do **not** need to re-enter them. `DOTNET_ENVIRONMENT=Development` is what makes
  the app load them — every start command below sets it.
- **Only one instance at a time.** Two running copies both long-poll Telegram and fight over it.
  Always stop a running instance (section 4) before starting a new one.
- Open **PowerShell in the repo root**:
  ```powershell
  cd "C:\Users\TenchoBostandzhiev\source\GitHub -Tencho Bostandzhiev\AI-NEWS-AGGREGATOR"
  ```

## Option A — Quick start (foreground, for a quick look)

Runs in the window; you see the live log; **closing the window or Ctrl+C stops it.**

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src\Newsroom.Worker -c Debug
```

## Option B — Detached start (keeps running after you close the terminal) ← use this

Build once, then launch the built .exe as its own process:

```powershell
dotnet build src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug   # rebuild after any code change
$env:DOTNET_ENVIRONMENT = 'Development'
$dir = Resolve-Path "src\Newsroom.Worker\bin\Debug\net10.0"
Start-Process -FilePath "$dir\Newsroom.Worker.exe" -WorkingDirectory $dir
```

It now runs independently. Logs go to `src\Newsroom.Worker\bin\Debug\net10.0\logs\newsroom-<date>.log`.

## 3. Check it is running

```powershell
Get-Process Newsroom.Worker -ErrorAction SilentlyContinue   # a row = running; nothing = stopped
# last few log lines (detached / Option B path):
Get-Content (Get-ChildItem "src\Newsroom.Worker\bin\Debug\net10.0\logs\newsroom-*.log" |
    Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Tail 6
```

Healthy signs: a recent `Heartbeat OK` line (every ~15s), `Publishing in FACEBOOK-ONLY mode`, and
`Scrape cycle` lines. `AI temporarily unavailable … will retry later` is harmless (Gemini free-tier
busy).

## 4. Stop it

```powershell
Get-Process Newsroom.Worker -ErrorAction SilentlyContinue | Stop-Process -Force
```

(Or, if you used Option A, just Ctrl+C in its window.)

## 5. Publishing mode (what it does when running)

Currently set (in user-secrets) to **Facebook-only, live**:

- `Publishing:FacebookOnly = true` → skips the website; approved drafts post straight to the FB page.
- `Facebook:DryRun = false` → posts are **real**.

Change it (then restart — section 4 then Option B):

```powershell
# Pause REAL posting (posts get logged, not sent):
dotnet user-secrets set "Facebook:DryRun" "true" --project src\Newsroom.Worker

# Resume real posting:
dotnet user-secrets set "Facebook:DryRun" "false" --project src\Newsroom.Worker

# Later: go back to the full website → Facebook pipeline (needs the Umbraco secrets set):
dotnet user-secrets set "Publishing:FacebookOnly" "false" --project src\Newsroom.Worker
```

## Notes

- **After changing code**, rebuild before Option B (the `dotnet build` line). Stop the running
  instance first — a running worker locks the DLL and the build fails.
- **Drafting is paused** if you set it so via Telegram `/pause`; `/resume` to re-enable. Scraping and
  Telegram review run regardless.
- The `dotnet run` (Option A) working dir is `src\Newsroom.Worker`, so its logs and `editor-uploads`
  live there; the detached .exe (Option B) uses `bin\Debug\net10.0`. Editor photo uploads are read
  from disk at publish time, so keep starting it the same way (don't mix A and B mid-review).
