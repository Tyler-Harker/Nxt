#requires -Version 5.1
<#
.SYNOPSIS
    One-shot Nxt installer for Windows.

.DESCRIPTION
    Packs the three NuGet packages (Runtime, CLI, project template), registers the local
    nupkg directory as a NuGet source so `nxt new` apps can restore Nxt.Runtime, installs
    the CLI globally, and installs the project template into `dotnet new`.

    Run from the repo root in PowerShell:
        .\install-windows.ps1

    Idempotent: re-running just reinstalls the latest source.
#>

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
Set-Location $Root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "x dotnet SDK not found on PATH." -ForegroundColor Red
    Write-Host "  Install .NET 10 SDK from https://dotnet.microsoft.com/download"
    exit 1
}

$NupkgDir = Join-Path $Root 'nupkg'
New-Item -ItemType Directory -Force -Path $NupkgDir | Out-Null

Write-Host "-> Packing Nxt.Runtime, Nxt.Cli, Nxt.Templates..."
dotnet pack src/Nxt.Runtime   -c Release -o $NupkgDir --nologo -v quiet | Out-Null
dotnet pack src/Nxt.Cli       -c Release -o $NupkgDir --nologo -v quiet | Out-Null
dotnet pack src/Nxt.Templates -c Release -o $NupkgDir --nologo -v quiet | Out-Null

Write-Host "-> Registering local NuGet source 'nxt-local'..."
dotnet nuget remove source nxt-local 2>$null | Out-Null
dotnet nuget add source $NupkgDir --name nxt-local | Out-Null

Write-Host "-> Clearing NuGet caches (so same-version re-pack is picked up)..."
dotnet nuget locals all --clear | Out-Null

Write-Host "-> Installing the 'nxt' global tool..."
dotnet tool uninstall -g Nxt.Cli 2>$null | Out-Null
dotnet tool install -g --add-source $NupkgDir Nxt.Cli | Out-Null

Write-Host "-> Installing the project template..."
dotnet new uninstall Nxt.Templates 2>$null | Out-Null
$TemplatePkg = Get-ChildItem -Path $NupkgDir -Filter 'Nxt.Templates.*.nupkg' | Select-Object -First 1
dotnet new install $TemplatePkg.FullName | Out-Null

# PATH hint — %USERPROFILE%\.dotnet\tools is where global tools land but isn't always on PATH.
if (-not (Get-Command nxt -ErrorAction SilentlyContinue)) {
    $ToolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'
    $UserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($UserPath -notlike "*$ToolsDir*") {
        Write-Host "-> Adding $ToolsDir to user PATH..."
        [Environment]::SetEnvironmentVariable('Path', "$UserPath;$ToolsDir", 'User')
        Write-Host "   Restart PowerShell to pick up the new PATH."
    }
}

Write-Host ""
Write-Host "Installed. Try:" -ForegroundColor Green
Write-Host "    nxt new my-app && cd my-app && nxt dev"
