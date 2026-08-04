# Sandbox Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A second worker instance that runs the full pipeline against a sandbox database, sends review cards to a separate Telegram bot, publishes approved articles to the local Umbraco site, and cannot reach the Predel News Facebook page even if misconfigured.

**Architecture:** A new `Sandbox` hosting environment selects a committed `appsettings.Sandbox.json` and its own dotnet user-secrets store (`newsroom-worker-sandbox`); the live `Development` store is structurally unreachable. `SandboxOptions.Violations` is a pure fail-closed guard that refuses startup when the database, site URL or image root still look live, and `Program.cs` force-overrides `Facebook:DryRun` on and `Publishing:FacebookOnly` off rather than trusting configuration. A `SandboxTelegramGateway` decorator marks every outgoing message. The live worker moves out of `bin/Debug` to `C:\apps\newsroom` so the two instances stop fighting over locked DLLs. Spec: `docs/superpowers/specs/2026-08-04-sandbox-mode-design.md`.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Hosting` 10.0.9, `Microsoft.Extensions.Configuration.UserSecrets` 10.0.9 (already transitive — no package reference to add), `Microsoft.Data.SqlClient` 7.0.2 (already referenced by `Newsroom.Infrastructure`), xUnit 2.9.3, Serilog, Windows PowerShell 5.1 for the scripts.

## Global Constraints

- **Never commit.** The owner reviews and commits himself after each work block. End every task with build clean + tests green, then stop. (This overrides any skill default that says to commit.)
- **Commits carry no `Co-Authored-By: Claude` trailer and no AI-attribution footer.** If the owner asks for a commit, write the message without them.
- **Never edit files with PowerShell `Get-Content`/`Set-Content`** — PS 5.1 corrupts UTF-8 Cyrillic. Use the Edit/Write file tools only. Several target files contain Cyrillic.
- **A running worker locks `bin/Debug/net10.0`, so `dotnet build` fails while one is running.** Task 2 permanently fixes this by moving the live worker to `C:\apps\newsroom`. Until Task 2 is done, stop the live worker before building (`Get-Process Newsroom.Worker | Stop-Process -Force`) and restart it afterwards.
- **The live pipeline must keep running.** Do not change its configuration, its secrets or its `DOTNET_ENVIRONMENT=Development`. Task 2 changes only the folder it runs from.
- **No new NuGet packages and no new csproj entries.** `appsettings.Sandbox.json` is picked up automatically by the Worker SDK's `Content Include="**\*.json"` glob with `CopyToOutputDirectory="PreserveNewest"` (`Microsoft.NET.Sdk.Worker.props`).
- The sandbox secrets store must **never** contain `Facebook:PageId` or `Facebook:AccessToken`.
- Local SQL Server has only the **default** instance (`MSSQLSERVER`), so every connection string uses `Server=.` — not `Server=.\SQLEXPRESS`, which appears in `appsettings.json` and is overridden by `appsettings.Development.json`.
- The local Umbraco site is `https://localhost:44350` (the `Umbraco.Web.UI` profile in the Predel-News repo).
- Code style: file-scoped namespaces, primary constructors, `/// <summary>` comments that explain *why*. Match `src/Newsroom.Infrastructure/Publishing/*.cs`.
- Build: `dotnet build Newsroom.slnx` · Tests: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj`

---

### Task 1: ADR-0014 records the sandbox decision

This repo is documentation-first (`README.md`): important decisions are recorded as an ADR before or with the code implementing them. This task adds no code.

**Files:**
- Create: `docs/adr/0014-sandbox-mode.md`
- Modify: `docs/adr/README.md`

- [ ] **Step 1: Read the ADR template and an existing ADR for the house format**

Read `docs/adr/template.md` and `docs/adr/0008-facebook-page-only.md`. Match their heading structure, status line and tone exactly — do not invent a new format.

- [ ] **Step 2: Write the ADR**

Create `docs/adr/0014-sandbox-mode.md` following the template's sections. The content to record:

- **Context:** There is no development environment. The live pipeline *is* a local dev run out of `src\Newsroom.Worker\bin\Debug\net10.0` with `DOTNET_ENVIRONMENT=Development`, which is exactly what loads the user-secrets holding the real Telegram bot, the real Gemini key, `Facebook:DryRun=false` and the live page token. Any second run inherits all of it, plus the live `Newsroom` database and the shared default image root `%ProgramData%\PredelNewsroom\images`, whose files a second `RetentionJob` would delete.
- **Decision:** A `Sandbox` hosting environment running side by side with live, isolated by (a) a separate user-secrets store that the live environment never loads and that holds no Facebook credentials, and (b) a fail-closed startup guard that refuses to run when the database name does not end `_Sandbox`, the site URL is not localhost, or the image root does not contain `sandbox`. `Facebook:DryRun` is forced on and `Publishing:FacebookOnly` forced off in code, overriding configuration rather than trusting it. The live worker moves to `C:\apps\newsroom` so the instances no longer contend for locked DLLs.
- **Consequences:** A second BotFather bot is mandatory (Telegram long polling is per token — two pollers on one token fight over `getUpdates`). Both instances share the Gemini free-tier key, so the sandbox consumes the live daily allowance; contained by small per-stage `DailyRequestBudget` values, not eliminated. The guard protects destinations, not the Telegram chat: the worker cannot recognise the editors' chat id, so a sandbox pointed at it would post there — visibly marked, but posted. `dotnet build` and F5 stop interrupting the live pipeline.
- **Alternatives rejected:** config discipline alone (one copied secret reaches the live page); one-at-a-time profile swapping (pauses the live pipeline during every development session); a stub publisher instead of the local Umbraco (would not exercise the real `NewsroomPublishingApiController` contract).

- [ ] **Step 3: Index the ADR**

Add the `0014-sandbox-mode` row to the table in `docs/adr/README.md`, matching the existing rows' column layout.

- [ ] **Step 4: Verify**

Re-read `docs/adr/0014-sandbox-mode.md` end to end. It must contain no "TBD" and every section the template requires. Report the file paths to the owner; do not commit.

---

### Task 2: Move the live worker to `C:\apps\newsroom`

This unblocks every later task: once live no longer runs from `bin/Debug`, builds and F5 stop requiring the pipeline to be killed.

**Files:**
- Modify: `tools/restart-worker.ps1`
- Modify: `docs/runbooks/start-the-worker.md`

**Interfaces:**
- Produces: the live worker running from `C:\apps\newsroom\Newsroom.Worker.exe`, still `DOTNET_ENVIRONMENT=Development`, still reading the `dotnet-Newsroom.Worker-d340c1d6-7d34-4b48-817f-b2e928a25019` secrets store. Task 7's `restart-sandbox.ps1` relies on live no longer occupying `bin/Debug`.

- [ ] **Step 1: Read the current script in full**

Read `tools/restart-worker.ps1`. Note its structure: stop running process → elevate on access denied → `dotnet build` → set `DOTNET_ENVIRONMENT` → `Start-Process` hidden → tail the newest log. The rewrite keeps all of that and changes only *where* it builds to and runs from, plus *how* it matches the process.

- [ ] **Step 2: Rewrite the script**

Replace `tools/restart-worker.ps1` with this. Note `-LiveRoot` is a parameter so the owner can point it elsewhere, and the process match is by `.Path` so it can never kill a sandbox instance:

```powershell
<#
.SYNOPSIS
    Releases and restarts the live Newsroom worker from its own folder
    (docs/runbooks/start-the-worker.md).
.DESCRIPTION
    The live worker runs from $LiveRoot, NOT from src\Newsroom.Worker\bin\Debug — that folder
    belongs to development builds and the sandbox F5 profile (docs/adr/0014-sandbox-mode.md).
    Keeping them apart means a dotnet build no longer has to kill the live pipeline.
    Stops only processes whose executable lives under $LiveRoot, publishes Debug on top, then
    relaunches detached with no window. Logs go to $LiveRoot\logs\newsroom-<date>.log.
.EXAMPLE
    .\tools\restart-worker.ps1
#>
[CmdletBinding()]
param(
    [string]$LiveRoot = "C:\apps\newsroom"
)

$ErrorActionPreference = "Stop"

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Set-Location $repoRoot

    # Match on path, never on name alone: a sandbox worker is the same executable name.
    $running = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($LiveRoot, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Host "Stopping the live worker (PID $($running.Id -join ', '))..."
        try {
            $running | Stop-Process -Force -ErrorAction Stop
        }
        catch {
            # An instance started from an elevated prompt can only be killed by an elevated one.
            Write-Host "Access denied - asking for elevation (accept the UAC prompt)..."
            $ids = ($running.Id | ForEach-Object { "/PID $_" }) -join ' '
            Start-Process -FilePath "taskkill.exe" -ArgumentList "/F $ids" -Verb RunAs -Wait -WindowStyle Hidden
        }
        Start-Sleep -Seconds 2
        $still = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($LiveRoot, [StringComparison]::OrdinalIgnoreCase) }
        if ($still) {
            throw "Could not stop the live worker. Stop it from an elevated PowerShell: Get-Process Newsroom.Worker | Stop-Process -Force"
        }
    }
    else {
        Write-Host "No live worker running - starting fresh."
    }

    Write-Host "Publishing to '$LiveRoot'..."
    dotnet publish src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug -o $LiveRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    # Development is what makes the app load the LIVE dotnet user-secrets (Gemini, Telegram,
    # Facebook). The sandbox uses DOTNET_ENVIRONMENT=Sandbox and a different secrets store.
    $env:DOTNET_ENVIRONMENT = 'Development'
    Write-Host "Starting hidden from '$LiveRoot'..."
    Start-Process -FilePath "$LiveRoot\Newsroom.Worker.exe" -WorkingDirectory $LiveRoot -WindowStyle Hidden

    Start-Sleep -Seconds 5
    $proc = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($LiveRoot, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $proc) { throw "Worker did not stay up - check the newest log under '$LiveRoot\logs'." }
    Write-Host "Live worker running (PID $($proc.Id -join ', '))."

    $log = Get-ChildItem "$LiveRoot\logs\newsroom-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host "--- $($log.Name) (last 6 lines) ---"
        Get-Content $log.FullName -Tail 6
    }
    else {
        Write-Host "No log file yet - check '$LiveRoot\logs' in a minute."
    }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
```

- [ ] **Step 3: Ask the owner to run the one-time cutover**

This step touches the live pipeline, so **stop and hand it to the owner** rather than running it unattended. The instruction to give:

```powershell
.\tools\restart-worker.ps1
```

Expected: the old `bin\Debug` instance is *not* matched (it is outside `C:\apps\newsroom`), so it must be stopped by hand first:

```powershell
Get-Process Newsroom.Worker | Stop-Process -Force
.\tools\restart-worker.ps1
```

- [ ] **Step 4: Verify the live worker is healthy in its new home**

```powershell
(Get-Process Newsroom.Worker).Path
```
Expected: `C:\apps\newsroom\Newsroom.Worker.exe`.

```powershell
Get-Content (Get-ChildItem C:\apps\newsroom\logs\newsroom-*.log | Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Tail 30
```
Expected: migrations applied, jobs started, **no** `Sandbox` banner, no errors.

Then confirm the review loop still works: send `/status` to the live Telegram bot and expect the usual reply.

- [ ] **Step 5: Confirm `bin/Debug` is free**

```powershell
dotnet build Newsroom.slnx
```
Expected: succeeds with no file-lock error — this is the payoff and the precondition for every later task.

- [ ] **Step 6: Update the runbook**

Edit `docs/runbooks/start-the-worker.md` so its paths match reality: the live worker lives in `C:\apps\newsroom`, its logs in `C:\apps\newsroom\logs`, and `bin/Debug` is now development-only. Add a short "Rolling back" note: stop the process, run the previous script form (`dotnet build` + start from `src\Newsroom.Worker\bin\Debug\net10.0`), which still works because nothing else changed.

Do not commit. Report to the owner.

---

### Task 3: `SandboxOptions` — the fail-closed guard

**Files:**
- Create: `src/Newsroom.Infrastructure/Operations/SandboxOptions.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Operations/SandboxOptionsTests.cs` (new folder)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces, all used by Task 6's `Program.cs` wiring:
  - `const string SandboxOptions.EnvironmentName = "Sandbox"`
  - `const string SandboxOptions.UserSecretsId = "newsroom-worker-sandbox"`
  - `SandboxOptions.From(IConfiguration) -> SandboxOptions` with `bool Enabled { get; init; }`
  - `SandboxOptions.Violations(string connectionString, string umbracoBaseUrl, string imageStorageRoot) -> IReadOnlyList<string>` — empty means safe
  - `SandboxOptions.DatabaseName(string connectionString) -> string?` — null when unparseable

- [ ] **Step 1: Write the failing tests**

Create `src/tests/Newsroom.Infrastructure.Tests/Operations/SandboxOptionsTests.cs`:

```csharp
using Newsroom.Infrastructure.Operations;

namespace Newsroom.Infrastructure.Tests.Operations;

public class SandboxOptionsTests
{
    private const string SandboxDb =
        "Server=.;Database=Newsroom_Sandbox;Integrated Security=True;TrustServerCertificate=True";
    private const string LiveDb =
        "Server=.;Database=Newsroom;Integrated Security=True;TrustServerCertificate=True";
    private const string SandboxRoot = @"C:\apps\newsroom-sandbox\images";
    private const string LiveRoot = @"C:\ProgramData\PredelNewsroom\images";

    [Fact]
    public void A_fully_isolated_configuration_has_no_violations() =>
        Assert.Empty(SandboxOptions.Violations(SandboxDb, "https://localhost:44350", SandboxRoot));

    [Theory]
    [InlineData("https://localhost:44350")]
    [InlineData("https://LOCALHOST:44350")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("https://localhost:44350/")]
    public void Local_site_urls_are_accepted(string url) =>
        Assert.Empty(SandboxOptions.Violations(SandboxDb, url, SandboxRoot));

    [Fact]
    public void The_live_database_is_refused()
    {
        var violations = SandboxOptions.Violations(LiveDb, "https://localhost:44350", SandboxRoot);
        Assert.Single(violations);
        Assert.Contains("Newsroom", violations[0]);
    }

    [Fact]
    public void A_connection_string_without_a_database_is_refused() =>
        Assert.Single(SandboxOptions.Violations(
            "Server=.;Integrated Security=True", "https://localhost:44350", SandboxRoot));

    [Fact]
    public void An_unparseable_connection_string_is_refused() =>
        Assert.Single(SandboxOptions.Violations(
            "this is not a connection string", "https://localhost:44350", SandboxRoot));

    [Theory]
    [InlineData("https://predel.news")]
    [InlineData("https://www.predel.news/umbraco")]
    public void A_public_site_url_is_refused(string url)
    {
        var violations = SandboxOptions.Violations(SandboxDb, url, SandboxRoot);
        Assert.Single(violations);
        Assert.Contains("localhost", violations[0]);
    }

    [Fact]
    public void A_site_url_that_is_not_absolute_is_refused() =>
        Assert.Single(SandboxOptions.Violations(SandboxDb, "localhost:44350", SandboxRoot));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(LiveRoot)]
    public void An_image_root_that_is_not_a_sandbox_root_is_refused(string root)
    {
        var violations = SandboxOptions.Violations(SandboxDb, "https://localhost:44350", root);
        Assert.Single(violations);
        Assert.Contains("Images:StorageRoot", violations[0]);
    }

    [Fact]
    public void The_sandbox_marker_in_the_image_root_is_case_insensitive() =>
        Assert.Empty(SandboxOptions.Violations(
            SandboxDb, "https://localhost:44350", @"D:\Newsroom-SANDBOX\images"));

    [Fact]
    public void Every_violation_is_reported_together_not_just_the_first() =>
        Assert.Equal(3, SandboxOptions.Violations(LiveDb, "https://predel.news", LiveRoot).Count);

    [Fact]
    public void DatabaseName_reads_the_catalog_and_reports_unparseable_as_null()
    {
        Assert.Equal("Newsroom_Sandbox", SandboxOptions.DatabaseName(SandboxDb));
        Assert.Null(SandboxOptions.DatabaseName("this is not a connection string"));
    }
}
```

Note why `Assert.Single` matters: it proves one bad value produces exactly one message, so the "all violations together" test is meaningful rather than accidental.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SandboxOptionsTests"`
Expected: build FAILS with `The type or namespace name 'SandboxOptions' could not be found`.

- [ ] **Step 3: Implement `SandboxOptions`**

Create `src/Newsroom.Infrastructure/Operations/SandboxOptions.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Newsroom.Infrastructure.Operations;

/// <summary>
/// Sandbox mode (ADR-0014): a second worker instance that develops against the full pipeline
/// while the live one keeps running. Isolation is not left to configuration discipline —
/// <see cref="Violations"/> is checked at startup and the worker refuses to run while any of
/// them holds, so a copied connection string or a forgotten base URL fails fast instead of
/// consuming the live review queue or publishing to the real site.
/// <para>
/// The checks are deliberately fails-closed (a *positive* assertion that each destination looks
/// like a sandbox one) rather than a blocklist of live values: an unset
/// <c>Images:StorageRoot</c> resolves to the shared <c>%ProgramData%</c> default, and a blocklist
/// would wave it through.
/// </para>
/// </summary>
public sealed record SandboxOptions
{
    /// <summary>The <c>DOTNET_ENVIRONMENT</c> value that selects appsettings.Sandbox.json.</summary>
    public const string EnvironmentName = "Sandbox";

    /// <summary>The sandbox's own dotnet user-secrets store. Deliberately a readable string
    /// rather than a GUID so the runbook's <c>dotnet user-secrets --id</c> commands are typeable.
    /// The live store (the csproj's UserSecretsId) is only auto-loaded in the Development
    /// environment, so it is unreachable from here.</summary>
    public const string UserSecretsId = "newsroom-worker-sandbox";

    public const string RequiredDatabaseSuffix = "_Sandbox";
    public const string RequiredStorageRootMarker = "sandbox";

    public bool Enabled { get; init; }

    public static SandboxOptions From(IConfiguration configuration) => new()
    {
        Enabled = configuration.GetValue("Sandbox:Enabled", false),
    };

    /// <summary>Every way the configuration still points at something live. Empty = safe to run.
    /// All violations are reported together so a misconfigured sandbox is fixed in one pass.</summary>
    /// <param name="imageStorageRoot">The *resolved* root (ImageStorageOptions.Root), not the raw
    /// config value — an unset value must be judged by what it actually resolves to.</param>
    public static IReadOnlyList<string> Violations(
        string connectionString, string umbracoBaseUrl, string imageStorageRoot)
    {
        var violations = new List<string>();

        if (DatabaseName(connectionString) is not { } database)
        {
            violations.Add("ConnectionStrings:Newsroom is not a valid SQL Server connection string.");
        }
        else if (!database.EndsWith(RequiredDatabaseSuffix, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                $"ConnectionStrings:Newsroom points at database '{database}' — a sandbox database "
                + $"name must end with '{RequiredDatabaseSuffix}'. Running against the live database "
                + "would consume the live review queue and publish live drafts.");
        }

        if (!Uri.TryCreate(umbracoBaseUrl, UriKind.Absolute, out var site))
        {
            violations.Add($"Umbraco:BaseUrl ('{umbracoBaseUrl}') is not an absolute URL.");
        }
        else if (!IsLoopback(site.Host))
        {
            violations.Add(
                $"Umbraco:BaseUrl points at '{site.Host}' — a sandbox may only publish to "
                + "localhost or 127.0.0.1.");
        }

        if (string.IsNullOrWhiteSpace(imageStorageRoot)
            || !imageStorageRoot.Contains(RequiredStorageRootMarker, StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                $"Images:StorageRoot ('{imageStorageRoot}') must contain "
                + $"'{RequiredStorageRootMarker}' — otherwise the sandbox shares the live image "
                + "folder and its retention sweep deletes live files.");
        }

        return violations;
    }

    /// <summary>The connection string's database, or null when it will not parse — used by the
    /// guard above and by the startup banner.</summary>
    public static string? DatabaseName(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SandboxOptionsTests"`
Expected: all PASS.

If `A_connection_string_without_a_database_is_refused` fails, the cause is that `SqlConnectionStringBuilder` returns `""` for a missing catalog — `"".EndsWith("_Sandbox")` is false, so it should already be a violation. Do not "fix" it by special-casing empty; re-read the assertion instead.

- [ ] **Step 5: Full build and test sweep**

Run: `dotnet build Newsroom.slnx` then `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj`
Expected: build clean, every test green. Stop; do not commit.

---

### Task 4: `SandboxTelegramGateway` — the visible marker

**Files:**
- Create: `src/Newsroom.Infrastructure/Review/SandboxTelegramGateway.cs`
- Test: `src/tests/Newsroom.Infrastructure.Tests/Review/SandboxTelegramGatewayTests.cs` (new folder)

**Interfaces:**
- Consumes: `Newsroom.Core.Review.ITelegramGateway` (existing, 8 members).
- Produces, used by Task 6: `public sealed class SandboxTelegramGateway(ITelegramGateway inner) : ITelegramGateway`, plus `const string HtmlMarker = "🧪 <b>SANDBOX</b>"` and `const string CaptionMarker = "🧪 SANDBOX"`.

**Why a decorator and not a change inside `ReviewMessageRenderer`:** wrapping the gateway also covers `TelegramOperatorAlerts` — watchdog alerts, the daily digest and publish-failure alerts — so *everything* the sandbox emits is marked, not just review cards.

- [ ] **Step 1: Read the interface being decorated**

Read `src/Newsroom.Core/Review/Interfaces.cs` lines 1-65. All eight members and their exact parameter lists are there. Copy the signatures rather than retyping them from memory.

- [ ] **Step 2: Write the failing tests**

Create `src/tests/Newsroom.Infrastructure.Tests/Review/SandboxTelegramGatewayTests.cs`:

```csharp
using Newsroom.Core.Review;
using Newsroom.Infrastructure.Review;

namespace Newsroom.Infrastructure.Tests.Review;

public class SandboxTelegramGatewayTests
{
    /// <summary>Records the arguments the decorator forwarded, so tests assert on what the real
    /// gateway would have been asked to send.</summary>
    private sealed class RecordingGateway : ITelegramGateway
    {
        public string? Html { get; private set; }
        public string? Caption { get; private set; }
        public long ChatId { get; private set; }
        public bool WithReviewButtons { get; private set; }
        public long? DraftId { get; private set; }
        public string? ScheduleLabel { get; private set; }
        public bool RemoveButtons { get; private set; }
        public long Offset { get; private set; }
        public string? CallbackId { get; private set; }
        public string? CallbackText { get; private set; }
        public string? FileId { get; private set; }
        public string? Directory { get; private set; }

        public Task<TgUpdateBatch> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
        {
            Offset = offset;
            // TgUpdateBatch(Callbacks, Texts, Photos, NextOffset) — src/Newsroom.Core/Review/TgUpdate.cs
            return Task.FromResult(new TgUpdateBatch([], [], [], offset));
        }

        public Task<long> SendHtmlAsync(long chatId, string html, bool withReviewButtons,
            long? draftIdForButtons, string? scheduleButtonLabel, CancellationToken ct)
        {
            (ChatId, Html, WithReviewButtons, DraftId, ScheduleLabel) =
                (chatId, html, withReviewButtons, draftIdForButtons, scheduleButtonLabel);
            return Task.FromResult(7L);
        }

        public Task EditHtmlAsync(long chatId, long messageId, string html, bool removeButtons,
            long? approveNowDraftIdForButton, CancellationToken ct)
        {
            (ChatId, Html, RemoveButtons, DraftId) = (chatId, html, removeButtons, approveNowDraftIdForButton);
            return Task.CompletedTask;
        }

        public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct)
        {
            (CallbackId, CallbackText) = (callbackId, text);
            return Task.CompletedTask;
        }

        public Task<long> SendPhotoAsync(long chatId, string photoUrlOrFileId, string? caption,
            long? draftIdForCycleButton, int? index, int? total, CancellationToken ct)
        {
            (ChatId, Caption, DraftId) = (chatId, caption, draftIdForCycleButton);
            return Task.FromResult(8L);
        }

        public Task EditPhotoAsync(long chatId, long messageId, string photoUrlOrFileId,
            string? caption, long? draftIdForCycleButton, CancellationToken ct)
        {
            (ChatId, Caption, DraftId) = (chatId, caption, draftIdForCycleButton);
            return Task.CompletedTask;
        }

        public Task<long> SendPhotoFileAsync(long chatId, string localPath, string? caption,
            long? draftIdForCycleButton, int? index, int? total, CancellationToken ct)
        {
            (ChatId, Caption, DraftId) = (chatId, caption, draftIdForCycleButton);
            return Task.FromResult(9L);
        }

        public Task<string> DownloadFileToAsync(string fileId, string directory, CancellationToken ct)
        {
            (FileId, Directory) = (fileId, directory);
            return Task.FromResult(@"C:\tmp\photo.jpg");
        }
    }

    private static (SandboxTelegramGateway Gateway, RecordingGateway Inner) Subject()
    {
        var inner = new RecordingGateway();
        return (new SandboxTelegramGateway(inner), inner);
    }

    [Fact]
    public async Task Sent_html_is_marked_and_every_other_argument_passes_through()
    {
        var (gateway, inner) = Subject();

        var messageId = await gateway.SendHtmlAsync(
            42, "<b>Заглавие</b>", withReviewButtons: true, draftIdForButtons: 5,
            scheduleButtonLabel: "📅 07:30", CancellationToken.None);

        Assert.Equal(7L, messageId);
        Assert.StartsWith(SandboxTelegramGateway.HtmlMarker, inner.Html);
        Assert.Contains("<b>Заглавие</b>", inner.Html);
        Assert.Equal(42, inner.ChatId);
        Assert.True(inner.WithReviewButtons);
        Assert.Equal(5, inner.DraftId);
        Assert.Equal("📅 07:30", inner.ScheduleLabel);
    }

    [Fact]
    public async Task Edited_html_is_marked_too_so_a_resolved_card_keeps_the_marker()
    {
        var (gateway, inner) = Subject();

        await gateway.EditHtmlAsync(42, 7, "✅ Одобрено", removeButtons: true,
            approveNowDraftIdForButton: null, CancellationToken.None);

        Assert.StartsWith(SandboxTelegramGateway.HtmlMarker, inner.Html);
        Assert.Contains("✅ Одобрено", inner.Html);
        Assert.True(inner.RemoveButtons);
    }

    [Fact]
    public async Task An_already_marked_message_is_not_marked_twice()
    {
        var (gateway, inner) = Subject();
        var once = $"{SandboxTelegramGateway.HtmlMarker}\n\nвече маркирано";

        await gateway.SendHtmlAsync(42, once, false, null, null, CancellationToken.None);

        Assert.Equal(once, inner.Html);
    }

    [Fact]
    public async Task Photo_captions_are_marked_on_send_edit_and_file_upload()
    {
        var (gateway, inner) = Subject();

        await gateway.SendPhotoAsync(42, "https://img/1.jpg", "снимка", 5, null, 3, CancellationToken.None);
        Assert.StartsWith(SandboxTelegramGateway.CaptionMarker, inner.Caption);

        await gateway.EditPhotoAsync(42, 8, "https://img/2.jpg", "друга", 5, CancellationToken.None);
        Assert.StartsWith(SandboxTelegramGateway.CaptionMarker, inner.Caption);

        await gateway.SendPhotoFileAsync(42, @"C:\img\cover.png", "корица", 5, null, 1, CancellationToken.None);
        Assert.StartsWith(SandboxTelegramGateway.CaptionMarker, inner.Caption);
    }

    [Fact]
    public async Task A_photo_with_no_caption_stays_without_one()
    {
        var (gateway, inner) = Subject();

        await gateway.SendPhotoAsync(42, "https://img/1.jpg", null, null, null, 1, CancellationToken.None);

        Assert.Null(inner.Caption);
    }

    [Fact]
    public async Task Non_sending_members_delegate_unchanged()
    {
        var (gateway, inner) = Subject();

        await gateway.GetUpdatesAsync(99, 25, CancellationToken.None);
        Assert.Equal(99, inner.Offset);

        await gateway.AnswerCallbackAsync("cb-1", "вече обработено", CancellationToken.None);
        Assert.Equal("cb-1", inner.CallbackId);
        Assert.Equal("вече обработено", inner.CallbackText);

        var path = await gateway.DownloadFileToAsync("file-1", @"C:\uploads", CancellationToken.None);
        Assert.Equal(@"C:\tmp\photo.jpg", path);
        Assert.Equal("file-1", inner.FileId);
        Assert.Equal(@"C:\uploads", inner.Directory);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SandboxTelegramGatewayTests"`
Expected: build FAILS with `The type or namespace name 'SandboxTelegramGateway' could not be found`.

- [ ] **Step 4: Implement the decorator**

Create `src/Newsroom.Infrastructure/Review/SandboxTelegramGateway.cs`:

```csharp
using Newsroom.Core.Review;

namespace Newsroom.Infrastructure.Review;

/// <summary>
/// Marks everything the sandbox sends (ADR-0014). The startup guard can check the database, the
/// site URL and the image root, but it cannot recognise the editors' chat id — no value in
/// configuration says which chat is the live one. So the guarantee is made *visible* instead: a
/// sandbox message is unmistakable at a glance, in the review cards and equally in the watchdog
/// alerts and daily digest that <see cref="Operations.TelegramOperatorAlerts"/> sends through the
/// same seam. Edits are marked too, so a resolved card does not silently lose the marker.
/// </summary>
public sealed class SandboxTelegramGateway(ITelegramGateway inner) : ITelegramGateway
{
    public const string HtmlMarker = "🧪 <b>SANDBOX</b>";
    public const string CaptionMarker = "🧪 SANDBOX";

    public Task<TgUpdateBatch> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct) =>
        inner.GetUpdatesAsync(offset, timeoutSeconds, ct);

    public Task<long> SendHtmlAsync(
        long chatId, string html, bool withReviewButtons, long? draftIdForButtons,
        string? scheduleButtonLabel, CancellationToken ct) =>
        inner.SendHtmlAsync(
            chatId, Mark(html, HtmlMarker)!, withReviewButtons, draftIdForButtons,
            scheduleButtonLabel, ct);

    public Task EditHtmlAsync(
        long chatId, long messageId, string html, bool removeButtons,
        long? approveNowDraftIdForButton, CancellationToken ct) =>
        inner.EditHtmlAsync(
            chatId, messageId, Mark(html, HtmlMarker)!, removeButtons,
            approveNowDraftIdForButton, ct);

    public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct) =>
        inner.AnswerCallbackAsync(callbackId, text, ct); // a toast is not a message in the chat

    public Task<long> SendPhotoAsync(
        long chatId, string photoUrlOrFileId, string? caption, long? draftIdForCycleButton,
        int? index, int? total, CancellationToken ct) =>
        inner.SendPhotoAsync(
            chatId, photoUrlOrFileId, Mark(caption, CaptionMarker), draftIdForCycleButton,
            index, total, ct);

    public Task EditPhotoAsync(
        long chatId, long messageId, string photoUrlOrFileId, string? caption,
        long? draftIdForCycleButton, CancellationToken ct) =>
        inner.EditPhotoAsync(
            chatId, messageId, photoUrlOrFileId, Mark(caption, CaptionMarker),
            draftIdForCycleButton, ct);

    public Task<long> SendPhotoFileAsync(
        long chatId, string localPath, string? caption, long? draftIdForCycleButton,
        int? index, int? total, CancellationToken ct) =>
        inner.SendPhotoFileAsync(
            chatId, localPath, Mark(caption, CaptionMarker), draftIdForCycleButton,
            index, total, ct);

    public Task<string> DownloadFileToAsync(string fileId, string directory, CancellationToken ct) =>
        inner.DownloadFileToAsync(fileId, directory, ct);

    /// <summary>Prefixes once. A photo with no caption keeps none — the review card that
    /// accompanies it already carries the marker, and inventing a caption would change layout.</summary>
    private static string? Mark(string? text, string marker) =>
        string.IsNullOrEmpty(text) || text.StartsWith(marker, StringComparison.Ordinal)
            ? text
            : $"{marker}\n\n{text}";
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SandboxTelegramGatewayTests"`
Expected: all PASS.

- [ ] **Step 6: Full build and test sweep**

Run: `dotnet build Newsroom.slnx` then `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj`
Expected: build clean, every test green. Stop; do not commit.

---

### Task 5: Sandbox configuration files

**Files:**
- Create: `src/Newsroom.Worker/appsettings.Sandbox.json`
- Modify: `src/Newsroom.Worker/Properties/launchSettings.json`
- Modify: `.gitignore`

**Interfaces:**
- Produces: the `Sandbox:Enabled` key that Task 6's guard reads, and the `Sandbox` launch profile.

- [ ] **Step 1: Create `appsettings.Sandbox.json`**

This file is committed and must contain **no secrets**. Note `Server=.` — the machine has only the default SQL instance, so the `SQLEXPRESS` spelling in `appsettings.json` would not connect.

```json
{
  "ConnectionStrings": {
    "Newsroom": "Server=.;Database=Newsroom_Sandbox;Integrated Security=True;TrustServerCertificate=True;Encrypt=True"
  },
  "Sandbox": {
    "Enabled": true
  },
  "Worker": {
    "HeartbeatSeconds": 30
  },
  "Scrape": {
    "CheckSeconds": 300
  },
  "Ai": {
    "Stages": {
      "Analyse": { "DailyRequestBudget": 40 },
      "Cluster": { "DailyRequestBudget": 3 },
      "Draft": { "DailyRequestBudget": 4 },
      "SelfCheck": { "DailyRequestBudget": 2 },
      "Image": { "DailyRequestBudget": 3 }
    }
  },
  "Images": {
    "StorageRoot": "C:\\apps\\newsroom-sandbox\\images"
  },
  "Umbraco": {
    "BaseUrl": "https://localhost:44350"
  },
  "Facebook": {
    "DryRun": true
  },
  "Publishing": {
    "FacebookOnly": false
  },
  "Serilog": {
    "WriteTo": [
      {},
      { "Args": { "path": "logs/sandbox-.log" } }
    ]
  }
}
```

Two things worth understanding rather than copying blindly:

- The small `DailyRequestBudget` values exist because the sandbox shares the live Gemini key and would otherwise eat the live pipeline's daily allowance. `Scrape:CheckSeconds` is raised for the same reason applied to the news sites.
- The `Serilog:WriteTo` array has an empty first element on purpose. `IConfiguration` merges JSON arrays by index, so `{}` leaves index 0 (the Console sink from `appsettings.json`) untouched while index 1 overrides only the File sink's `path`.

- [ ] **Step 2: Add the `Sandbox` launch profile**

In `src/Newsroom.Worker/Properties/launchSettings.json`, add a second profile alongside the existing `Newsroom.Worker` one:

```json
    "Sandbox": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Sandbox"
      }
    }
```

Keep the existing profile untouched. The file is UTF-8 with a BOM — use the Edit tool, not PowerShell.

- [ ] **Step 3: Ignore the sandbox publish folder**

Append to `.gitignore`:

```
# Sandbox worker publish output (tools/restart-sandbox.ps1, ADR-0014)
.sandbox/
```

- [ ] **Step 4: Verify the config file reaches the output**

Run: `dotnet build Newsroom.slnx`
Then: `Get-ChildItem src\Newsroom.Worker\bin\Debug\net10.0\appsettings*.json | Select-Object Name`
Expected: `appsettings.json`, `appsettings.Development.json`, **and** `appsettings.Sandbox.json` — proving the SDK glob picked it up with no csproj change.

- [ ] **Step 5: Verify the JSON parses**

Run: `Get-Content src\Newsroom.Worker\appsettings.Sandbox.json -Raw | ConvertFrom-Json | Out-Null; if ($?) { "valid JSON" }`
Expected: `valid JSON`. (Reading for validation is fine; never *write* these files with PowerShell.)

Stop; do not commit.

---

### Task 6: Wire sandbox mode into `Program.cs`

**Files:**
- Modify: `src/Newsroom.Worker/Program.cs`

**Interfaces:**
- Consumes: `SandboxOptions.EnvironmentName`, `SandboxOptions.UserSecretsId`, `SandboxOptions.From`, `SandboxOptions.Violations`, `SandboxOptions.DatabaseName` (Task 3); `SandboxTelegramGateway` (Task 4); `Sandbox:Enabled` (Task 5).
- Produces: a worker that refuses to start when sandbox mode is armed and any destination looks live, and that logs a banner naming every effective destination.

Four separate edits, described in file order. Read `src/Newsroom.Worker/Program.cs` in full first — it is a single top-level-statements file of about 195 lines.

- [ ] **Step 1: Add the sandbox secrets store and read the flag**

Add `using Newsroom.Infrastructure.Operations;` to the using block (it may already be there for `JobHeartbeat`/`OperationsRepository` — check before adding a duplicate).

Immediately after `var builder = Host.CreateApplicationBuilder(args);` and **before** `builder.Services.AddWindowsService(...)`:

```csharp
    // Sandbox mode (ADR-0014). Host.CreateApplicationBuilder only auto-loads dotnet user-secrets
    // in the Development environment, so under Sandbox the LIVE store is never read; the
    // sandbox's own store is added explicitly and, being appended last, wins over the JSON files.
    if (builder.Environment.EnvironmentName == SandboxOptions.EnvironmentName)
        builder.Configuration.AddUserSecrets(SandboxOptions.UserSecretsId);

    var sandbox = SandboxOptions.From(builder.Configuration);
```

Order matters: the secrets store is added *before* `SandboxOptions.From` reads the flag.

- [ ] **Step 2: Add the fail-closed guard**

Immediately after the existing `var connectionString = builder.Configuration.GetConnectionString("Newsroom") ?? throw ...;` statement:

```csharp
    // Fail closed: a sandbox still pointing at a live destination must not reach a job. All
    // violations are reported at once so the config is fixed in one pass.
    if (sandbox.Enabled)
    {
        var violations = SandboxOptions.Violations(
            connectionString,
            builder.Configuration.GetValue("Umbraco:BaseUrl", "")!,
            ImageStorageOptions.From(builder.Configuration).Root);
        if (violations.Count > 0)
            throw new InvalidOperationException(
                $"Sandbox mode refused to start:{Environment.NewLine}  - "
                + string.Join($"{Environment.NewLine}  - ", violations));
    }
```

`ImageStorageOptions` comes from `Newsroom.Infrastructure.Images`, already in the using block. Passing the *resolved* `.Root` (not the raw config value) is what makes an unset `Images:StorageRoot` fail — it resolves to the shared `%ProgramData%` default.

- [ ] **Step 3: Force the publishing overrides**

Replace the three publishing options registrations (currently `AddSingleton(UmbracoOptions.From(...))`, `AddSingleton(FacebookOptions.From(...))`, `AddSingleton(PublishingOptions.From(...))`) with:

```csharp
    builder.Services.AddSingleton(UmbracoOptions.From(builder.Configuration));

    // Sandbox overrides configuration rather than trusting it (ADR-0014): DryRun on means
    // FacebookPublisher takes its existing dry-run branch and never calls the Graph API, whatever
    // a stray token in configuration says; FacebookOnly off is required because it would
    // otherwise skip the Umbraco leg entirely — and publishing to the local site is the point.
    var facebookOptions = FacebookOptions.From(builder.Configuration);
    var publishingOptions = PublishingOptions.From(builder.Configuration);
    if (sandbox.Enabled)
    {
        facebookOptions = facebookOptions with { DryRun = true };
        publishingOptions = publishingOptions with { FacebookOnly = false };
    }
    builder.Services.AddSingleton(facebookOptions);
    builder.Services.AddSingleton(publishingOptions);
```

- [ ] **Step 4: Decorate the Telegram gateway**

Replace the existing `Lazy<ITelegramGateway>` registration with:

```csharp
    builder.Services.AddSingleton(_ => new Lazy<ITelegramGateway>(() =>
    {
        ITelegramGateway gateway = new TelegramGateway(
            TelegramOptions.From(builder.Configuration).BotToken
                ?? throw new InvalidOperationException("Telegram:BotToken is not configured."));
        // Wrapping the gateway (not the renderer) also marks watchdog alerts and the daily digest.
        return sandbox.Enabled ? new SandboxTelegramGateway(gateway) : gateway;
    }));
```

- [ ] **Step 5: Log the startup banner**

Replace `var host = builder.Build();` and the `host.Run();` that follows it with:

```csharp
    var host = builder.Build();

    if (sandbox.Enabled)
    {
        // Warning level and first in the log: the fastest way to notice a sandbox pointed
        // somewhere unintended. Every destination is named explicitly.
        host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
            "🧪 SANDBOX MODE — database {Database}, Telegram chat {ChatId}, site {Site}, "
            + "Facebook dry-run (forced), images {ImageRoot}",
            SandboxOptions.DatabaseName(connectionString) ?? "(unparseable)",
            TelegramOptions.From(builder.Configuration).ReviewChatId,
            builder.Configuration.GetValue("Umbraco:BaseUrl", ""),
            ImageStorageOptions.From(builder.Configuration).Root);
    }

    host.Run();
```

`ILogger<Program>` resolves against the implicit `Program` class that top-level statements generate; it is accessible within this assembly.

- [ ] **Step 6: Verify the live path is unchanged**

Run: `dotnet build Newsroom.slnx`
Expected: clean.

Then prove `Sandbox:Enabled=false` changes nothing:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src\Newsroom.Worker\Newsroom.Worker.csproj
```

Expected: normal startup, **no** `SANDBOX MODE` line. Stop it with Ctrl+C after the jobs report started.

**Careful:** this runs a second worker against the live database and the live Telegram bot for a few seconds. It will contend with the live poller. Keep it short, or ask the owner to stop the live worker first. If in doubt, skip this step and rely on Step 7 plus the live worker's own log.

- [ ] **Step 7: Verify the guard actually refuses**

Prove the fail-closed path with a deliberately live-looking database, without touching any committed file:

```powershell
$env:DOTNET_ENVIRONMENT = 'Sandbox'
$env:ConnectionStrings__Newsroom = 'Server=.;Database=Newsroom;Integrated Security=True;TrustServerCertificate=True'
dotnet run --project src\Newsroom.Worker\Newsroom.Worker.csproj
```

Expected: exits non-zero with `Sandbox mode refused to start:` and a bullet naming database `Newsroom`. No job starts, nothing connects to Telegram.

Then clear the override so it does not leak into later steps:

```powershell
Remove-Item Env:\ConnectionStrings__Newsroom
Remove-Item Env:\DOTNET_ENVIRONMENT
```

(The double underscore is how .NET maps environment variables onto the `ConnectionStrings:Newsroom` key. It is added by `CreateApplicationBuilder` before the sandbox user-secrets store, so the store would win — which is why this test uses a key the sandbox store does not set.)

- [ ] **Step 8: Full test sweep**

Run: `dotnet test src/tests/Newsroom.Infrastructure.Tests/Newsroom.Infrastructure.Tests.csproj` and `dotnet test src/tests/Newsroom.Core.Tests/Newsroom.Core.Tests.csproj`
Expected: all green. Stop; do not commit.

---

### Task 7: `restart-sandbox.ps1` and the sandbox runbook

**Files:**
- Create: `tools/restart-sandbox.ps1`
- Create: `docs/runbooks/run-the-sandbox.md`

**Interfaces:**
- Consumes: Task 2's relocated live worker (so `.sandbox` and `C:\apps\newsroom` never collide), Task 5's `appsettings.Sandbox.json`, Task 6's guard and banner.
- Produces: the sandbox running from `<repo>\.sandbox\Newsroom.Worker.exe`.

- [ ] **Step 1: Write the script**

Create `tools/restart-sandbox.ps1`:

```powershell
<#
.SYNOPSIS
    Restarts the sandbox Newsroom worker (docs/runbooks/run-the-sandbox.md, ADR-0014).
.DESCRIPTION
    The sandbox runs from its own folder so it never contends with the live worker in
    C:\apps\newsroom for locked DLLs, and both scripts match processes by executable PATH so
    neither can kill the other's instance. Configuration comes from appsettings.Sandbox.json plus
    the 'newsroom-worker-sandbox' user-secrets store; the worker refuses to start unless the
    database, the site URL and the image root are all sandbox ones.
    Only one sandbox instance may run at a time (the F5 'Sandbox' profile is the other one) -
    two would fight over the sandbox bot's getUpdates.
.EXAMPLE
    .\tools\restart-sandbox.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

try {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Set-Location $repoRoot
    $sandboxRoot = Join-Path $repoRoot ".sandbox"

    $running = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($sandboxRoot, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Host "Stopping the sandbox worker (PID $($running.Id -join ', '))..."
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }

    # A debugger-launched sandbox (the F5 'Sandbox' profile) shares this bot and database.
    $fromBin = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path -like "*\bin\Debug\*" }
    if ($fromBin) {
        throw "A worker is running from bin\Debug (PID $($fromBin.Id -join ', ')) - that is the F5 sandbox. Stop it first; two sandboxes fight over the same Telegram bot."
    }

    Write-Host "Publishing to '$sandboxRoot'..."
    dotnet publish src\Newsroom.Worker\Newsroom.Worker.csproj -c Debug -o $sandboxRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $env:DOTNET_ENVIRONMENT = 'Sandbox'
    Write-Host "Starting the sandbox hidden from '$sandboxRoot'..."
    Start-Process -FilePath "$sandboxRoot\Newsroom.Worker.exe" -WorkingDirectory $sandboxRoot -WindowStyle Hidden

    Start-Sleep -Seconds 6
    $proc = Get-Process Newsroom.Worker -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($sandboxRoot, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $proc) {
        Write-Host "Sandbox did not stay up - the guard most likely refused it. Newest log:"
        $failed = Get-ChildItem "$sandboxRoot\logs\*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($failed) { Get-Content $failed.FullName -Tail 20 }
        throw "Sandbox worker exited. Fix the reported violations and run again."
    }
    Write-Host "Sandbox running (PID $($proc.Id -join ', '))."

    $log = Get-ChildItem "$sandboxRoot\logs\sandbox-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) {
        Write-Host "--- $($log.Name) (last 10 lines - look for the SANDBOX MODE banner) ---"
        Get-Content $log.FullName -Tail 10
    }
    else {
        Write-Host "No sandbox log yet - check '$sandboxRoot\logs' in a minute."
    }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
```

- [ ] **Step 2: Write the runbook**

Read an existing runbook (`docs/runbooks/start-the-worker.md`) for the house format, then create `docs/runbooks/run-the-sandbox.md` covering, in this order:

**First-time setup** (each step with its exact command):

1. Create the sandbox bot: message `@BotFather`, `/newbot`, name it so it is obviously not the live one (e.g. `Predel Newsroom SANDBOX`). Keep the token. Then send the new bot any message and read your own numeric user id and the chat id — the runbook should say to use `https://api.telegram.org/bot<TOKEN>/getUpdates` for that.
2. Create the database:
   ```powershell
   sqlcmd -S . -Q "IF DB_ID('Newsroom_Sandbox') IS NULL CREATE DATABASE Newsroom_Sandbox;"
   ```
   Migrations run inside the worker at startup, so nothing else is needed.
3. Seed the sources:
   ```powershell
   sqlcmd -S . -d Newsroom_Sandbox -i tools\seed-sources.sql
   ```
4. Create the image folder: `New-Item -ItemType Directory -Force C:\apps\newsroom-sandbox\images`
5. Set the sandbox secrets — note `--id`, which is what keeps them out of the live store, and note that **no Facebook keys are set**:
   ```powershell
   dotnet user-secrets set --id newsroom-worker-sandbox "Telegram:BotToken" "<sandbox bot token>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Telegram:ReviewChatId" "<your chat id>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Telegram:AllowedUserIds:0" "<your user id>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Ai:Gemini:ApiKey" "<same key as live>"
   dotnet user-secrets set --id newsroom-worker-sandbox "Umbraco:ClientSecret" "<a new secret you choose>"
   ```
   Optionally the Pixabay/Pexels/Cloudflare keys.
6. On the site side, in the Predel-News repo, set the matching secret so `NewsroomPublishingSetup` provisions the `newsroom-bot` API user, the "Predel News" author and the placeholder cover on first start:
   ```powershell
   dotnet user-secrets set "PredelNews:Newsroom:ClientSecret" "<the same secret>"
   ```

**Daily loop:**

1. Start the local site (F5 on `PredelNews.Web`, `Umbraco.Web.UI` profile → `https://localhost:44350`).
2. `.\tools\restart-sandbox.ps1`.
3. Confirm the `🧪 SANDBOX MODE` banner in the tailed log names `Newsroom_Sandbox`, the sandbox chat, `https://localhost:44350` and `C:\apps\newsroom-sandbox\images`.
4. Review cards arrive in the sandbox chat prefixed `🧪 SANDBOX`. Approve one; the article appears on the local site and the log shows `Facebook dry run for draft {id}`.

**Rules and gotchas** to state explicitly:

- Only one sandbox at a time — the script and the F5 `Sandbox` profile share the bot and the database.
- Never put `Facebook:PageId` or `Facebook:AccessToken` in the sandbox store.
- The sandbox shares the live Gemini key, so it consumes the live daily allowance; the deliberately small `Ai:Stages:*:DailyRequestBudget` values in `appsettings.Sandbox.json` bound it. Raise them for a heavy session and put them back afterwards.
- The guard checks destinations, not the chat: a sandbox pointed at the editors' chat *would* post there, marked. Double-check `Telegram:ReviewChatId`.
- Drafts take a while to appear on a fresh database — the sandbox has to scrape, analyse, cluster and draft first, on small budgets.

- [ ] **Step 3: Verify the script's failure path first**

Before any secrets exist the guard should still let the worker start (the guard checks destinations, not credentials) but Telegram stays dormant. To prove the *guard* works end to end, temporarily break it: edit `src/Newsroom.Worker/appsettings.Sandbox.json` to `"BaseUrl": "https://predel.news"`, run `.\tools\restart-sandbox.ps1`, and expect the script to report `Sandbox worker exited` with the violation naming `predel.news` in the log tail. **Restore the file to `https://localhost:44350` immediately afterwards.**

- [ ] **Step 4: Verify the happy path**

Complete the first-time setup from the runbook, then run `.\tools\restart-sandbox.ps1`.

Expected: `Sandbox running (PID …)` and a log tail containing the `🧪 SANDBOX MODE` banner with `Newsroom_Sandbox`, the sandbox chat id, `https://localhost:44350` and `C:\apps\newsroom-sandbox\images`.

- [ ] **Step 5: Verify the two scripts cannot kill each other**

With both instances running:

```powershell
Get-Process Newsroom.Worker | Select-Object Id, Path
```
Expected: exactly two rows, one under `C:\apps\newsroom` and one under `<repo>\.sandbox`.

```powershell
.\tools\restart-sandbox.ps1
Get-Process Newsroom.Worker | Where-Object { $_.Path -like "C:\apps\newsroom*" }
```
Expected: the live PID is **unchanged** — the sandbox restart did not touch it.

- [ ] **Step 6: Verify the log file name**

```powershell
Get-ChildItem .sandbox\logs\*.log | Select-Object Name
```
Expected: `sandbox-<date>.log`. If it is `newsroom-<date>.log`, the `Serilog:WriteTo` array override in `appsettings.Sandbox.json` did not merge — harmless, but fix it by giving index 0 its explicit `{ "Name": "Console" }` value rather than `{}`.

Stop; do not commit.

---

### Task 8: End-to-end proof and the remaining documentation

**Files:**
- Modify: `docs/09-deployment.md`
- Modify: `docs/06-security.md`
- Modify: `docs/08-testing.md`
- Modify: `docs/README.md`
- Modify: `docs/decision-log.md`

- [ ] **Step 1: Prove the whole loop by hand**

With the local Umbraco running and the sandbox started:

1. Wait for a review card in the sandbox chat. It must be prefixed `🧪 SANDBOX`.
2. Tap ✅ Одобри.
3. Check the sandbox log for the Umbraco publish success line and open the returned URL — it must be on `https://localhost:44350`.
4. Check the sandbox log for `Facebook dry run for draft {id}` and confirm there is **no** Graph API call and nothing new on the real page.
5. Confirm the live worker is unaffected: its own log is still ticking and its PID is unchanged.

Record the outcome. If any step fails, stop and report — do not paper over it in the docs.

- [ ] **Step 2: Update `docs/09-deployment.md`**

The environments table's "Local dev" row currently describes an aspiration. Rewrite it to describe what now exists: `DOTNET_ENVIRONMENT=Sandbox`, `Newsroom_Sandbox` database, the `newsroom-worker-sandbox` secrets store, a separate bot, `https://localhost:44350`, forced Facebook dry-run, run via `tools/restart-sandbox.ps1`. Add that the live worker now runs from `C:\apps\newsroom` rather than `bin/Debug`, and cross-reference ADR-0014.

- [ ] **Step 3: Update `docs/06-security.md`**

Add the two-secrets-stores rule: `Development` loads the live store automatically; `Sandbox` loads `newsroom-worker-sandbox` explicitly and never the live one; the sandbox store must never hold `Facebook:PageId` or `Facebook:AccessToken`. Note that user-secrets are per Windows user profile, so a service account would not see them.

- [ ] **Step 4: Update `docs/08-testing.md`**

Add the sandbox as the manual end-to-end harness, with the Step 1 checklist above as the procedure, and note that the automated coverage for it is `SandboxOptionsTests` and `SandboxTelegramGatewayTests`.

- [ ] **Step 5: Update `docs/README.md` and `docs/decision-log.md`**

Index `docs/runbooks/run-the-sandbox.md` in the documentation map alongside the other runbooks, and add ADR-0014 to the ADR list. Add a dated entry to `docs/decision-log.md` matching the file's existing entry format: 2026-08-04, sandbox mode adopted, live worker relocated to `C:\apps\newsroom`.

- [ ] **Step 6: Final sweep**

Run: `dotnet build Newsroom.slnx` and both test projects.
Expected: clean and green.

Then `git status` and read the full diff. Confirm: no secret in any committed file, `appsettings.Sandbox.json` contains no token, `.sandbox/` is ignored, and the live `appsettings.json` / `appsettings.Development.json` are untouched.

Report to the owner with the list of changed files and the end-to-end result from Step 1. Do not commit.
