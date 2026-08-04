# Design — Sandbox mode: a second worker that publishes to localhost and can never reach Facebook

**Date:** 2026-08-04 · **Status:** Approved (pending spec review) · **Related:**
docs/09-deployment.md (environments table), docs/06-security.md (secrets are not configuration),
docs/07-operations.md, docs/05-integrations/umbraco.md, ADR-0007, ADR-0008

## Problem

There is no development environment. The "live" pipeline **is** a local dev run:

- no `PredelNewsroom` service is installed and `C:\apps\newsroom` does not exist;
- the running worker is `src\Newsroom.Worker\bin\Debug\net10.0\Newsroom.Worker.exe`, started by
  `tools/restart-worker.ps1` with `DOTNET_ENVIRONMENT=Development`;
- `Development` is precisely what loads the dotnet user-secrets store holding the real Telegram
  bot, the real Gemini key, `Facebook:DryRun=false`, `Publishing:FacebookOnly=true` and the live
  page token.

So today any second run started for development inherits the live secrets, the live `Newsroom`
database, the live Telegram chat and a live Facebook token. Two further consequences fall out of
the same fact:

- the live worker holds a lock on the DLLs in `bin\Debug\net10.0`, so **every `dotnet build`
  requires killing the live pipeline** — which is exactly what `tools/restart-worker.ps1` does;
- `Images:StorageRoot` is unset everywhere, so both a live and a dev run would resolve to the same
  default root (`%ProgramData%\PredelNewsroom\images`) and the dev run's `RetentionJob` would
  delete live image files.

The editor wants to develop against the full pipeline — real Telegram review cards, tap Approve —
with the article landing on the **local** Umbraco site and nothing whatsoever reaching the Predel
News Facebook page.

Owner decisions (2026-08-04):

- The sandbox runs **side by side** with the live pipeline, not instead of it.
- The sandbox runs the **full pipeline** (scrape → analyse → cluster → draft → review → publish)
  and **shares the live Gemini API key**; a second key was considered and declined for now.
- The "never touch Facebook" guarantee is **belt and braces**: a separate secrets store holding no
  Facebook token *and* a code-enforced guard that overrides configuration.
- Both a launcher script and a Visual Studio F5 profile.

## Goal

A `Sandbox` environment that a developer can start alongside the live worker, that produces real
Telegram review cards in a separate chat, publishes approved articles to
`https://localhost:44350`, and that **fails to start** rather than run if any of its destinations
looks like a production one.

## Non-goals (YAGNI)

- No staging VPS — `docs/09-deployment.md` already rules that out for v1.
- No seed/copy script for sandbox data. The sandbox scrapes and drafts for itself (owner decision:
  full pipeline).
- No second Gemini API key and no cross-instance budget coordination. Containment is per-instance
  `DailyRequestBudget` values only.
- No stub/fake publishers. The sandbox uses the real `UmbracoPublisher` against a real local
  Umbraco, and the real `FacebookPublisher` in its existing dry-run path.
- No guard on the Telegram bot token or chat id (the worker cannot know what the live values are).
  Separation there is the separate secrets store plus the visible message marker in §4.3.
- No change to the live worker's configuration, secrets or behaviour — only the folder it runs from
  (§4.4).

## Design

### 1. The `Sandbox` environment and where its configuration comes from

`DOTNET_ENVIRONMENT=Sandbox` selects a new committed **`src/Newsroom.Worker/appsettings.Sandbox.json`**.
It carries no secrets and is safe in git. The Worker SDK's default content glob is
`Content Include="**\*.json"` with `CopyToOutputDirectory="PreserveNewest"`
(`Microsoft.NET.Sdk.Worker.props`), so the new file is copied to the output with **no csproj
change**.

`Host.CreateApplicationBuilder` only auto-loads user-secrets when the environment is
`Development`. Under `Sandbox` the live store is therefore **not read at all**. `Program.cs`
instead adds a *different* store explicitly, immediately after the builder is created:

```csharp
if (builder.Environment.EnvironmentName == SandboxOptions.EnvironmentName)
    builder.Configuration.AddUserSecrets(SandboxOptions.UserSecretsId);
```

`Microsoft.Extensions.Configuration.UserSecrets` 10.0.9 is already in the Worker's dependency
graph (transitively via `Microsoft.Extensions.Hosting`), so no package reference is added. The
secrets id is the readable string **`newsroom-worker-sandbox`** rather than a GUID, so the runbook
commands are typeable:

```
dotnet user-secrets set --id newsroom-worker-sandbox "Telegram:BotToken" "<sandbox bot token>"
```

Being appended last, this provider wins over `appsettings.Sandbox.json`. The live store
(`dotnet-Newsroom.Worker-d340c1d6-…`, declared as `UserSecretsId` in the csproj) is untouched and
structurally unreachable from the sandbox.

### 2. `SandboxOptions` — fail-closed guard and forced overrides

New `src/Newsroom.Infrastructure/Operations/SandboxOptions.cs`:

```csharp
public sealed record SandboxOptions
{
    public const string EnvironmentName = "Sandbox";
    public const string UserSecretsId = "newsroom-worker-sandbox";
    public const string RequiredDatabaseSuffix = "_Sandbox";
    public const string RequiredStorageRootMarker = "sandbox";

    public bool Enabled { get; init; }

    public static SandboxOptions From(IConfiguration configuration) => new()
    {
        Enabled = configuration.GetValue("Sandbox:Enabled", false),
    };

    /// <summary>Every way the configuration still points at something live. Empty = safe.</summary>
    public static IReadOnlyList<string> Violations(
        string connectionString, string umbracoBaseUrl, string imageStorageRoot);
}
```

`Violations` is pure (string in, strings out) so it is unit-testable without a host. It reports
**all** violations at once rather than the first, so a misconfigured sandbox is fixed in one pass:

| Check | Rule | Why |
| --- | --- | --- |
| Database | `SqlConnectionStringBuilder.InitialCatalog` must end with `_Sandbox` | Fails **closed** — `Newsroom` cannot be reached even by typo. A sandbox on the live database would consume the live review queue, publish live drafts and mark them Published. |
| Site | `Umbraco:BaseUrl` host must be `localhost` or `127.0.0.1` | The whole point: publishes land on the developer's machine. |
| Images | `Images:StorageRoot` must be non-empty and contain `sandbox` (ordinal, case-insensitive) | Fails closed against the shared `%ProgramData%\PredelNewsroom\images` default, whose files the sandbox's `RetentionJob` would otherwise delete. |

`Microsoft.Data.SqlClient` 7.0.2 is already referenced by `Newsroom.Infrastructure`, so
`SqlConnectionStringBuilder` needs no new dependency. A connection string that will not parse is
itself a violation.

`Program.cs`, once the connection string is resolved and the options records are bound:

1. If `Sandbox:Enabled` is false → nothing below happens; the live path is byte-for-byte as today.
2. Collect violations; if any, throw `InvalidOperationException` listing them all. The existing
   `catch` around the host start logs it as `Fatal` and returns exit code 1 — the sandbox never
   reaches a job.
3. **Override**, not merely validate — configuration is not trusted:
   - `FacebookOptions` is registered as `facebookOptions with { DryRun = true }`. Both are `sealed
     record`s with `init` properties, so this is a one-line non-destructive mutation. Every
     Facebook path — `PublishJob`, the `Facebook:TestPostDraftId` hook in
     `FacebookTestPostService`, `WatchdogJob`'s token check — then runs through
     `FacebookPublisher`'s existing dry-run branch, which logs the would-be post and returns
     `DryRunPostId` without an HTTP call.
   - `PublishingOptions` is registered as `publishingOptions with { FacebookOnly = false }`.
     Without this the sandbox would inherit the live `FacebookOnly=true` posture and skip the
     Umbraco leg entirely — nothing would ever reach the local site, which is the feature.
4. Log a banner at **Warning** level naming every effective destination: database, Telegram chat
   id, Umbraco base URL, Facebook mode, image storage root, and the per-stage AI budgets. This is
   the first thing in the sandbox log and the fastest way to spot a mistake.

### 3. Telegram: a second bot, and a visible marker

A second BotFather bot is not a preference but a requirement: Telegram long polling is per **token**,
so two workers on one token fight over `getUpdates` and each would swallow half the button presses.
The sandbox bot's token, review chat id and allowed user id live in the sandbox secrets store.

Because the worker cannot verify that a chat id is *not* the editors' chat, the guarantee is made
visible instead. New `src/Newsroom.Infrastructure/Review/SandboxTelegramGateway.cs`, a decorator
over `ITelegramGateway`:

- `SendHtmlAsync`, `EditHtmlAsync` — prefix the HTML with `🧪 <b>SANDBOX</b>\n\n`;
- `SendPhotoAsync`, `SendPhotoFileAsync`, `EditPhotoAsync` — prefix the caption the same way;
- every other member (`GetUpdatesAsync`, `AnswerCallbackAsync`, `DownloadFileToAsync`) delegates
  unchanged.

Edits are prefixed too, so a resolved card does not silently lose the marker. The decorator wraps
inside the existing `Lazy<ITelegramGateway>` registration in `Program.cs`, which means it also
covers `TelegramOperatorAlerts` (watchdog alerts, the daily digest, publish-failure alerts) —
everything the sandbox emits is marked, not just review cards.

### 4. Where each worker lives on disk

The two instances cannot share an output folder: the running one locks the DLLs and the other's
build fails. The live worker therefore moves out of `bin/Debug`, to the location
`docs/09-deployment.md` already designates.

| | Path | Environment | Secrets store | Logs |
| --- | --- | --- | --- | --- |
| Live | `C:\apps\newsroom` (publish output) | `Development` | `dotnet-Newsroom.Worker-d340c1d6-…` (unchanged) | `C:\apps\newsroom\logs\newsroom-*.log` |
| Sandbox (script) | `<repo>\.sandbox` (gitignored) | `Sandbox` | `newsroom-worker-sandbox` | `<repo>\.sandbox\logs\sandbox-*.log` |
| Sandbox (F5) | `bin\Debug\net10.0` | `Sandbox` | `newsroom-worker-sandbox` | `bin\Debug\net10.0\logs\sandbox-*.log` |

Consequences worth stating plainly:

- `dotnet build` and F5 stop interrupting the live pipeline — the reason the current
  `restart-worker.ps1` has to kill the worker before building disappears.
- Live keeps `DOTNET_ENVIRONMENT=Development` and the same user-secrets id, so **no secret is
  migrated or retyped**. User-secrets are per user profile, not per folder, so running from
  `C:\apps\newsroom` as the same account reads exactly the same store. (If the live worker is ever
  installed as a Windows service under a *different* account it will stop seeing them — that
  migration belongs to `docs/runbooks/deploy.md`, not here, and is called out there.)
- The script and F5 sandboxes share one database and one bot, so **only one may run at a time**.
  The runbook says so and `restart-sandbox.ps1` reports a running instance rather than guessing.

`appsettings.Sandbox.json` overrides the Serilog file sink path to `logs/sandbox-.log` (config key
`Serilog:WriteTo:1:Args:path`; index 1 is the `File` sink in the base file). The folders already
differ — this is purely so a tailed log can never be mistaken for the wrong instance.

### 5. Scripts and the F5 profile

**`tools/restart-sandbox.ps1`** (new) mirrors the existing script's structure:

1. Stop a running sandbox instance, matched on `(Get-Process Newsroom.Worker).Path` under
   `<repo>\.sandbox` — never by name alone.
2. `dotnet publish src\Newsroom.Worker -c Debug -o .sandbox`.
3. Set `DOTNET_ENVIRONMENT=Sandbox` and start `.sandbox\Newsroom.Worker.exe` hidden.
4. Tail the newest `.sandbox\logs\sandbox-*.log`, which begins with the §2.4 banner.

**`tools/restart-worker.ps1`** (changed) publishes to and runs from `C:\apps\newsroom`, and its
kill step matches on that path instead of on the process name. After both changes neither script
can terminate the other's process.

**`launchSettings.json`** gains a `Sandbox` profile — `commandName: Project`,
`DOTNET_ENVIRONMENT=Sandbox` — for breakpoints in `PublishJob` / `UmbracoPublisher`.

`.gitignore` gains `.sandbox/`.

### 6. Containing the shared Gemini key

The sandbox burns the live pipeline's daily allowance, and the local ledger already misreads
Google's real accounting (a 429 arrived on 2026-07-30 while the counter showed headroom).
Containment is config-only, in `appsettings.Sandbox.json`:

- deliberately small `Ai:Stages:*:DailyRequestBudget` values — Analyse 40, Cluster 3, Draft 4,
  SelfCheck 2, Image 3 — raisable by hand when a session needs more;
- `Scrape:CheckSeconds` raised to 300 so the sandbox does not double the outbound request rate to
  the Bulgarian news sites it shares with live.

This bounds the damage; it does not remove it. Expect the live pipeline to reach its daily quota
sooner on heavy development days. A second key on another Google account remains the real fix if
that starts to hurt.

## Configuration reference

`appsettings.Sandbox.json` (committed, no secrets):

| Key | Value | Note |
| --- | --- | --- |
| `Sandbox:Enabled` | `true` | Arms §2 |
| `ConnectionStrings:Newsroom` | `Server=.\SQLEXPRESS;Database=Newsroom_Sandbox;…` | Must end `_Sandbox` |
| `Umbraco:BaseUrl` | `https://localhost:44350` | Predel-News `Umbraco.Web.UI` profile |
| `Publishing:FacebookOnly` | `false` | Also force-overridden in code |
| `Facebook:DryRun` | `true` | Also force-overridden in code |
| `Images:StorageRoot` | `C:\apps\newsroom-sandbox\images` | Must contain `sandbox` |
| `Ai:Stages:*:DailyRequestBudget` | small | §6 |
| `Scrape:CheckSeconds` | `300` | §6 |
| `Serilog:WriteTo:1:Args:path` | `logs/sandbox-.log` | §4 |

Sandbox user-secrets (`--id newsroom-worker-sandbox`): `Telegram:BotToken`,
`Telegram:ReviewChatId`, `Telegram:AllowedUserIds:0`, `Ai:Gemini:ApiKey` (same key as live),
`Umbraco:ClientSecret`, and optionally the Pixabay/Pexels/Cloudflare keys. **No
`Facebook:PageId`, no `Facebook:AccessToken`** — an unconfigured `FacebookOptions.IsConfigured`
already leaves the Facebook leg dormant, before the forced dry-run is even consulted.

On the site side, Predel-News needs `PredelNews:Newsroom:ClientSecret` in **its** user-secrets
matching `Umbraco:ClientSecret` above; `NewsroomPublishingSetup` then idempotently provisions the
`newsroom-bot` API user, the "Predel News" author and the placeholder cover on first start.

## Error handling

| Situation | Behaviour |
| --- | --- |
| Sandbox points at the `Newsroom` database | Startup throws listing the violation; exit code 1; no job runs |
| `Umbraco:BaseUrl` is a public host | Same — all violations reported together |
| `Images:StorageRoot` unset (would resolve to the shared default) | Same |
| A Facebook token somehow present in sandbox secrets | `DryRun` forced on regardless; `FacebookPublisher` logs the would-be post, sends nothing, returns `DryRunPostId` |
| Local Umbraco not running | `UmbracoPublisher` gets a connection failure → transient → `PublishJob` retries next cycle and gives up at `Umbraco:MaxAttempts`, exactly as in production |
| Local Umbraco rejects the payload (400) | `PublishRejectedException` → permanent, alert to the sandbox chat — the real contract test |
| Both sandbox instances (script + F5) started | Telegram returns 409 on `getUpdates`; the runbook says run one |
| `Sandbox:Enabled` false / environment not `Sandbox` | Zero behaviour change; the live path is untouched |

## Testing

`Newsroom.Infrastructure.Tests`, following the existing per-area folder layout:

- **`Operations/SandboxOptionsTests`** — `Violations` accepts `Newsroom_Sandbox` +
  `https://localhost:44350` + a `sandbox` root; rejects `Newsroom`, rejects a non-localhost host,
  rejects an empty and a non-`sandbox` storage root, rejects an unparseable connection string;
  reports several violations together; `127.0.0.1` is accepted.
- **`Review/SandboxTelegramGatewayTests`** — over a fake `ITelegramGateway`: the marker is prefixed
  to `SendHtmlAsync` HTML, to `EditHtmlAsync` HTML and to photo captions; `GetUpdatesAsync`,
  `AnswerCallbackAsync` and `DownloadFileToAsync` delegate with arguments unchanged; buttons and
  draft ids pass through untouched.

The forced overrides in `Program.cs` are covered by the existing
`FacebookPublisherTests` dry-run cases — the override only chooses an already-tested branch. The
end-to-end proof is manual and belongs to the runbook: one draft approved in the sandbox chat
appears on `https://localhost:44350` and produces a `Facebook dry run for draft …` log line.

## Documentation

- **New ADR `docs/adr/0014-sandbox-mode.md`** — the decision: two side-by-side instances isolated
  by environment + separate secrets store, with a fail-closed startup guard; the rejected
  alternatives (config discipline alone; one-at-a-time profile swapping; a stub publisher).
- **New `docs/runbooks/run-the-sandbox.md`** — first-time setup (BotFather bot, `CREATE DATABASE
  Newsroom_Sandbox`, `tools/seed-sources.sql`, both secrets stores, starting the local Umbraco) and
  the daily loop.
- **`docs/runbooks/start-the-worker.md`** — live now runs from `C:\apps\newsroom`; one-time cutover
  steps.
- **`docs/09-deployment.md`** — the environments table's "Local dev" row becomes the sandbox as
  actually built; record that live has moved out of `bin/Debug`.
- **`docs/06-security.md`** — the second user-secrets store and the rule that the sandbox store
  never holds a Facebook token.
- **`docs/08-testing.md`** — the sandbox as the manual end-to-end harness.
- **`docs/README.md`** — index the new runbook and ADR.

## Risks

- **Quota contention** (§6) — accepted by the owner, contained but not eliminated.
- **The live relocation is a one-time cutover** with a few minutes of pipeline downtime, and
  `C:\apps\newsroom` must be writable by the running account. The runbook covers rolling back to
  `bin/Debug` if it misbehaves.
- **Doubled scraping load** on shared sources, mitigated by the sandbox's longer
  `Scrape:CheckSeconds`.
- **The guard protects destinations, not the Telegram chat.** A sandbox pointed at the editors'
  chat would post there — visibly marked `🧪 SANDBOX`, but it would post. Accepted: the worker
  has no way to recognise the live chat id.
