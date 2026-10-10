#requires -Version 7.0
<#
.SYNOPSIS
Regenerates the README and guide images from the fictional demo.
.DESCRIPTION
Every view, the tray preview and the details window are rendered off-screen by the demo build: no screen
capture, no window shown, no real account. showcase.py composes docs/hero.png and refreshes the guide captures;
tour.py rebuilds docs/tour.gif. Requires Python 3 with Pillow.
#>
[CmdletBinding()]
param(
    [string]$Dotnet,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name ffxiv_dx11,ffxiv -ErrorAction SilentlyContinue) { throw 'Final Fantasy XIV est en cours : visuels reportés.' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dotnet) { $Dotnet = & (Join-Path $PSScriptRoot 'find-dotnet.ps1') }
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $Dotnet).Source
& $Dotnet build (Join-Path $root 'src/CodexTracker.App/CodexTracker.App.csproj') -c Release
if ($LASTEXITCODE) { throw 'Compilation échouée.' }
$exe = Join-Path $root 'src/CodexTracker.App/bin/Release/net10.0-windows10.0.19041.0/CodexTracker.exe'
$out = Join-Path $root 'artifacts/previews'
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Invoke-Render([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $exe -ArgumentList (@('--demo', '--demo-claude', '--demo-resets', '--language', 'fr') + $Arguments) -PassThru -Wait
    if (-not (Test-Path -LiteralPath $File)) { throw "Rendu échoué : $File (code $($process.ExitCode))" }
    Write-Output $File
}
foreach ($theme in 'dark', 'light') {
    foreach ($view in 'Comptes', 'Resets', 'Semaine', 'Général', 'Rappels', 'Canaux', 'Assistants') {
        $file = Join-Path $out "$view-$theme-normal-96.png"
        Invoke-Render $file @('--preview', $file, '--view', $view, '--theme', $theme, '--size', 'normal', '--dpi', '96')
    }
    foreach ($capture in 'peek', 'details') {
        $file = Join-Path $out "$capture-$theme.png"
        Invoke-Render $file @('--theme', $theme, "--$capture-screenshot", $file, '--smoke-test')
    }
}
& $Python (Join-Path $PSScriptRoot 'showcase.py')
if ($LASTEXITCODE) { throw 'Composition des visuels échouée.' }
& $Python (Join-Path $PSScriptRoot 'tour.py')
if ($LASTEXITCODE) { throw 'Tour GIF échoué.' }
