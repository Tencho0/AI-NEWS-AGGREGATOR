# Runbook — Run the Sandbox (dev machine)

**Status:** Agreed · **Last updated:** 2026-08-04

How to set up and run the sandbox Newsroom worker — a second instance that develops against the
full pipeline (real scraping, real AI, real Telegram review, real publish) without ever being able
to touch the live database, the live site or the real Predel News Facebook page. Fail-closed
isolation is `SandboxOptions` and `SandboxTelegramGateway`; see
[ADR-0014](../adr/0014-sandbox-mode.md) for the design.

> **Topology: this dev machine runs the sandbox only.** The live pipeline runs on the
> Predel-News VPS (see [deploy.md](deploy.md) and
> [release-a-new-version.md](release-a-new-version.md)), not on this machine. Do **not** also
> start a `Development`-environment (live-configured) worker here — Telegram long polling is per
> bot token, and this dev machine's live user-secrets store points at the *same* bot token the
> VPS instance is already polling with. Two pollers on one token fight over `getUpdates`, and each
> one only sees and answers roughly half of the editors' button presses. If you need to interact
> with the live pipeline, do it on the VPS, not here.

> **Why `Sandbox` is the default launch profile:** `src\Newsroom.Worker\Properties\launchSettings.json`
> lists the `Sandbox` profile first, so `dotnet run` (or F5) with no profile picked applies
> `Sandbox`, not the live-pointing `Newsroom.Worker` profile. This is a safety net, not a
> convenience — a `dotnet run` that silently lands on `DOTNET_ENVIRONMENT=Development` loads the
> live secrets store from this dev machine, which is exactly the scenario the paragraph above
> warns about.

## First-time setup (once)

1. **Create the sandbox bot.** Message `@BotFather` on Telegram, send `/newbot`, and name it so it
   is obviously not the live one (e.g. `Predel Newsroom SANDBOX`). Keep the token it gives you.
   Then send the new bot any message and read your own numeric user id and the chat id from:
   ```
   https://api.telegram.org/bot<TOKEN>/getUpdates
   ```
2. **Create the database:**
   ```powershell
   sqlcmd -S . -Q "IF DB_ID('Newsroom_Sandbox') IS NULL CREATE DATABASE Newsroom_Sandbox;"
   ```
   Migrations run inside the worker at startup, so nothing else is needed here.
3. **Seed the sources:**
   ```powershell
   sqlcmd -S . -d Newsroom_Sandbox -i tools\seed-sources.sql
   ```
4. **Create the image folder:**
   ```powershell
   New-Item -ItemType Directory -Force C:\apps\newsroom-sandbox\images
   ```
5. **Set the sandbox secrets.** Note `--id newsroom-worker-sandbox`, which is what keeps these out
   of the live store entirely — and note that **no Facebook keys are set here**:
   ```powershell
   dotnet user-secrets set --id newsroom-worker-sandbox "Telegram:BotToken" "<sandbox bot token>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Telegram:ReviewChatId" "<your chat id>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Telegram:AllowedUserIds:0" "<your user id>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Ai:Gemini:ApiKey" "<same key as live>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Umbraco:ClientSecret" "<a new secret you choose>"
   ```
   Optionally also the Pixabay/Pexels/Cloudflare keys, the same way.

   > **Never** put `Facebook:PageId` or `Facebook:AccessToken` into the
   > `newsroom-worker-sandbox` secrets store. The sandbox forces `Facebook:DryRun=true` and
   > `Publishing:FacebookOnly=false` in code regardless of configuration (ADR-0014), but the store
   > should not hold live Facebook credentials in the first place.
6. **On the site side**, in the Predel-News repo, set the matching secret so
   `NewsroomPublishingSetup` provisions the `newsroom-bot` API user, the "Predel News" author and
   the placeholder cover on first start:
   ```powershell
   dotnet user-secrets set "PredelNews:Newsroom:ClientSecret" "<the same secret>"
   ```

## Daily loop

1. Start the local site: F5 on `PredelNews.Web`, `Umbraco.Web.UI` profile → `https://localhost:44350`.
2. Run:
   ```powershell
   .\tools\restart-sandbox.ps1
   ```
3. Confirm the `🧪 SANDBOX MODE` banner in the tailed log names `Newsroom_Sandbox`, the sandbox
   chat, `https://localhost:44350` and `C:\apps\newsroom-sandbox\images`.
4. Review cards arrive in the sandbox chat prefixed `🧪 SANDBOX`. Approve one; the article appears
   on the local site and the log shows `Facebook dry run for draft {id}`.

## Check it is running

```powershell
Get-Process Newsroom.Worker | Select-Object Id, Path   # look for the row under <repo>\.sandbox
Get-Content (Get-ChildItem .sandbox\logs\sandbox-*.log |
    Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Tail 10
```

## Stop it

```powershell
Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith((Resolve-Path .sandbox), [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
```

## Rules and gotchas

- **Only one sandbox at a time.** `tools\restart-sandbox.ps1` and the F5 `Sandbox` launch profile
  share the same bot and the same database — running both together makes them fight over
  `getUpdates` exactly like two live instances would. The script itself checks for and refuses to
  run alongside a `bin\Debug`-launched (F5) sandbox.
- **Never put `Facebook:PageId` or `Facebook:AccessToken` in the sandbox secrets store**
  (`--id newsroom-worker-sandbox`). There is no code path that reads them for the sandbox, but
  keeping the store free of them is the second line of defence, not the first.
- **The sandbox shares the live Gemini key**, so it consumes the live daily allowance. The
  deliberately small `Ai:Stages:*:DailyRequestBudget` values in `appsettings.Sandbox.json` bound
  that. Raise them for a heavy session and put them back afterwards.
- **The guard checks destinations, not the chat.** `SandboxOptions.Violations` can tell a live
  database or a public site URL from a sandbox one, but nothing in configuration says which
  Telegram chat is the editors' real one — a sandbox pointed at `Telegram:ReviewChatId` for the
  live chat *would* post there, clearly marked `🧪 SANDBOX`, but posted. Double-check that value
  before setting it.
- **Drafts take a while to appear on a fresh database** — the sandbox has to scrape, analyse,
  cluster and draft first, on the small budgets above.
- **This dev machine runs the sandbox only** (see the topology note above). Do not also start a
  `Development`-environment worker here — the live pipeline already runs on the VPS, and a second
  poller on the same bot token here would steal roughly half of the editors' button presses from
  the real one.

## Troubleshooting

- **Script reports `Sandbox worker exited`** — the fail-closed guard refused to start. The
  script's own tailed log shows the violation(s) (e.g. a database not ending `_Sandbox`, a site
  URL that is not `localhost`/`127.0.0.1`, or an image root without `sandbox` in it). Fix the
  named setting and run the script again.
- **Script reports a worker running from `bin\Debug`** — stop the F5-launched `Sandbox` instance
  (or vice versa) before rerunning; only one sandbox instance may run at a time.
- **Log file is `newsroom-<date>.log` instead of `sandbox-<date>.log`** — the
  `Serilog:WriteTo` array override in `appsettings.Sandbox.json` did not merge over the base
  Console sink at index 0. See the comment in that file.
