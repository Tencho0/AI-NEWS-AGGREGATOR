<#
.SYNOPSIS
    Verifies the server has the .NET runtime required by the newsroom worker (net10.0).
.DESCRIPTION
    The worker is published framework-dependent, so Microsoft.NETCore.App 10.x must be
    installed. Older majors (8/9) cannot run a net10.0 app - .NET never rolls forward
    across a lower major version. Exit code 0 = ready, 1 = runtime missing.
.EXAMPLE
    .\check-dotnet-runtime.ps1
#>
[CmdletBinding()]
param(
    [int]$RequiredMajor = 10
)

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "FAIL: 'dotnet' not found on PATH - no .NET runtime is installed." -ForegroundColor Red
    Write-Host "Install '.NET Runtime $RequiredMajor.0.x' (x64) from https://dotnet.microsoft.com/download/dotnet/$RequiredMajor.0"
    exit 1
}
Write-Host "dotnet found: $($dotnetCmd.Source)"

$runtimeLines = & dotnet --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not $runtimeLines) {
    Write-Host "FAIL: 'dotnet --list-runtimes' returned nothing - the installation looks broken." -ForegroundColor Red
    exit 1
}

$installed = @()
foreach ($line in $runtimeLines) {
    if ($line -match '^Microsoft\.NETCore\.App (\d+\.\d+\.\d+)') {
        $installed += [version]$Matches[1]
    }
}

if ($installed.Count -eq 0) {
    Write-Host "FAIL: no Microsoft.NETCore.App runtime found (only SDKs or other runtimes present)." -ForegroundColor Red
} else {
    Write-Host "Installed Microsoft.NETCore.App runtimes: $(($installed | Sort-Object | ForEach-Object { $_.ToString() }) -join ', ')"
}

$matching = @($installed | Where-Object { $_.Major -eq $RequiredMajor })
if ($matching.Count -gt 0) {
    $best = ($matching | Sort-Object -Descending)[0]
    Write-Host "OK: .NET $RequiredMajor runtime present ($best) - the worker will run." -ForegroundColor Green
    exit 0
}

Write-Host "FAIL: no .NET $RequiredMajor runtime installed." -ForegroundColor Red
Write-Host "An older runtime cannot run a net$RequiredMajor.0 app (no backward roll-forward)." -ForegroundColor Red
Write-Host "Fix: install '.NET Runtime $RequiredMajor.0.x' (x64) from https://dotnet.microsoft.com/download/dotnet/$RequiredMajor.0"
Write-Host "     (base runtime only - the SDK and ASP.NET Core runtime are not needed)"
exit 1
