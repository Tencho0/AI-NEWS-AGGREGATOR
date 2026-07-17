<#
.SYNOPSIS
    Lists every .NET installation on this machine: SDKs, modern runtimes, and .NET Framework.
.DESCRIPTION
    Inventory-only, changes nothing. Covers:
      - .NET SDKs            (dotnet --list-sdks)
      - .NET runtimes        (dotnet --list-runtimes: Microsoft.NETCore.App, AspNetCore, WindowsDesktop)
      - .NET Framework 4.x   (registry, Release DWORD mapped to a version)
    Works on Windows PowerShell 5.1. Run on any machine; no elevation needed.
.EXAMPLE
    .\list-dotnet-versions.ps1
#>
[CmdletBinding()]
param()

Write-Host "=== Modern .NET (Core / 5+) ===" -ForegroundColor Cyan

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "  'dotnet' not found on PATH - no modern .NET (Core/5+) is installed." -ForegroundColor Yellow
} else {
    Write-Host "  dotnet host: $($dotnetCmd.Source)"

    Write-Host ""
    Write-Host "  SDKs:" -ForegroundColor Cyan
    $sdks = & dotnet --list-sdks
    if ($LASTEXITCODE -eq 0 -and $sdks) {
        foreach ($sdk in $sdks) { Write-Host "    $sdk" }
    } else {
        Write-Host "    (none - runtime-only install)" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "  Runtimes:" -ForegroundColor Cyan
    $runtimes = & dotnet --list-runtimes
    if ($LASTEXITCODE -eq 0 -and $runtimes) {
        $groups = $runtimes | Group-Object { ($_ -split ' ')[0] }
        foreach ($group in $groups) {
            Write-Host "    $($group.Name):"
            foreach ($entry in $group.Group) {
                $version = ($entry -split ' ')[1]
                Write-Host "      $version"
            }
        }
    } else {
        Write-Host "    (none found)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "=== .NET Framework (4.x) ===" -ForegroundColor Cyan

$ndpKey = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
$ndp = Get-ItemProperty -Path $ndpKey -ErrorAction SilentlyContinue
if (-not $ndp -or -not $ndp.Release) {
    Write-Host "  .NET Framework 4.5+ not detected." -ForegroundColor Yellow
} else {
    $release = [int]$ndp.Release
    $frameworkVersion = switch ($true) {
        ($release -ge 533320) { '4.8.1'; break }
        ($release -ge 528040) { '4.8'; break }
        ($release -ge 461808) { '4.7.2'; break }
        ($release -ge 461308) { '4.7.1'; break }
        ($release -ge 460798) { '4.7'; break }
        ($release -ge 394802) { '4.6.2'; break }
        ($release -ge 394254) { '4.6.1'; break }
        ($release -ge 393295) { '4.6'; break }
        ($release -ge 379893) { '4.5.2'; break }
        ($release -ge 378675) { '4.5.1'; break }
        default               { '4.5' }
    }
    Write-Host "  .NET Framework $frameworkVersion (Release $release, build $($ndp.Version))"
}

Write-Host ""
Write-Host "Note: the newsroom worker needs the Microsoft.NETCore.App 10.x runtime (see check-dotnet-runtime.ps1)."
