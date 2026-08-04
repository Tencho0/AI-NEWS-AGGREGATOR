# Runbook — Start the Worker (dev machine)

**Status:** Agreed · **Last updated:** 2026-08-04

How to start / stop / check the Newsroom worker **on this dev machine, by yourself** — no Claude
session needed. (For the VPS/production install see [deploy.md](deploy.md) and
[restore-after-vps-restart.md](restore-after-vps-restart.md).)

> **Where it runs from:** the live worker runs from `C:\apps\newsroom`, not from
> `src\Newsroom.Worker\bin\Debug\net10.0` — that folder is now development/F5 and the sandbox
> profile only (see [ADR-0014](../adr/0014-sandbox-mode.md)). Keeping them apart means a
> `dotnet build` no longer has to kill the live pipeline.

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

> `dotnet run` applies a launch profile, and a launch profile's `environmentVariables` win over
> whatever `DOTNET_ENVIRONMENT` you export in the shell first. With no `--launch-profile` named,
> `dotnet run` applies whichever profile is *first* in `launchSettings.json` — currently `Sandbox`,
> not `Newsroom.Worker` (see [ADR-0014](../adr/0014-sandbox-mode.md)). `--launch-profile` below
> pins it explicitly, so this command loads `Development` regardless of profile order.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src\Newsroom.Worker -c Debug --launch-profile Newsroom.Worker
```

## Option B — Detached start (keeps running after you close the terminal) ← use this

Publish once to the live worker's own folder, `C:\apps\newsroom` (**not** `bin\Debug` — that's
development/sandbox-only now, see the note above), then launch the published .exe as its own
process. `-WindowStyle Hidden` means **no window at all**, so there is nothing to accidentally
close — you can shut every terminal and it keeps running:

```powershell
dotnet publish src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug -o C:\apps\newsroom   # after any code change
$env:DOTNET_ENVIRONMENT = 'Development'
Start-Process -FilePath "C:\apps\newsroom\Newsroom.Worker.exe" -WorkingDirectory "C:\apps\newsroom" -WindowStyle Hidden
```

**One-liner for the whole restart** — `tools\restart-worker.ps1` does exactly the above, after
stopping any running instance (section 4) and checking the new one stayed up:

```powershell
.\tools\restart-worker.ps1
```

If the running instance was started from an *elevated* prompt, a normal session cannot kill it; the
script asks for elevation (UAC) for the stop step only, so the worker comes back up unelevated.

It now runs independently. Logs go to `C:\apps\newsroom\logs\newsroom-<date>.log`
(there is no live console, so watch the log file — see section 3).

> **Which window can I close?** If you drop `-WindowStyle Hidden`, the worker opens its **own**
> console window showing live logs — closing *that* window (or Ctrl+C in it) **stops** the worker.
> The PowerShell window you *typed the launch command into* is separate; closing it is always safe.
> With `-WindowStyle Hidden` there is no worker window, so this whole question goes away — stop it
> via section 4 instead.

## 3. Check it is running

```powershell
Get-Process Newsroom.Worker -ErrorAction SilentlyContinue   # a row = running; nothing = stopped
# last few log lines (detached / Option B path):
Get-Content (Get-ChildItem "C:\apps\newsroom\logs\newsroom-*.log" |
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

## Rolling back

If `C:\apps\newsroom` is ever unusable and the worker needs to run from the old location, stop it
(section 4) and start it the pre-2026-08-04 way — nothing else about the app changed, so this
still works exactly as before:

```powershell
dotnet build src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug
$env:DOTNET_ENVIRONMENT = 'Development'
$dir = Resolve-Path "src\Newsroom.Worker\bin\Debug\net10.0"
Start-Process -FilePath "$dir\Newsroom.Worker.exe" -WorkingDirectory $dir -WindowStyle Hidden
```

`tools\restart-worker.ps1` will not manage an instance started this way — it only stops and checks
processes whose path is under `C:\apps\newsroom` (or whatever `-LiveRoot` you pass it) — so stop
and verify this one by hand.

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

- **After changing code**, republish before Option B (the `dotnet publish` line). Stop the running
  instance first — a running worker locks its own files under `C:\apps\newsroom` and the publish
  fails.
- **Drafting is paused** if you set it so via Telegram `/pause`; `/resume` to re-enable. Scraping and
  Telegram review run regardless.
- The `dotnet run` (Option A) working dir is `src\Newsroom.Worker`, so its logs and `editor-uploads`
  live there; the detached .exe (Option B) now runs from `C:\apps\newsroom` instead. Editor photo
  uploads are read from disk at publish time, so keep starting it the same way (don't mix A and B
  mid-review).
- `src\Newsroom.Worker\bin\Debug\net10.0` is development/F5 and sandbox territory only now (see
  [ADR-0014](../adr/0014-sandbox-mode.md)); it is no longer where the live worker runs from, which
  is why a plain `dotnet build` no longer needs the live pipeline stopped first.
