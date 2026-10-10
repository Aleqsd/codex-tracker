#requires -Version 7.0
<#
.SYNOPSIS
Installs a published Release on this PC through the tracker's own update engine.
.DESCRIPTION
Restarts the tracker in the background so it checks the Releases, waits for the verified package,
then restarts it again to install at startup. Checks the installed version, the update result
and that every account is kept. No window is opened; private data is neither copied nor printed.
Requires "Télécharger automatiquement" and "Installer au prochain démarrage" in Settings → Application.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version,
    [string]$Executable = (Join-Path $env:LOCALAPPDATA 'Programs\CodexTracker\CodexTracker.exe'),
    [int]$TimeoutMinutes = 20
)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'CodexTracker'
if (-not (Test-Path -LiteralPath $Executable)) { throw "Codex Tracker n’est pas installé ici : $Executable" }

function Get-InstalledVersion { ((Get-Item -LiteralPath $Executable).VersionInfo.ProductVersion -split '\+')[0] }
function Get-AccountIds { @((Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).accounts.id) }
function Get-Tracker {
    # The main instance only: MCP bridges and Claude Code collectors share the executable.
    Get-CimInstance Win32_Process -Filter "Name = 'CodexTracker.exe'" |
        Where-Object { $_.ExecutablePath -eq $Executable -and $_.CommandLine -notmatch '--(mcp|claude-)' }
}
function Restart-Tracker {
    Start-Process -FilePath $Executable -ArgumentList '--exit' -Wait
    $deadline = (Get-Date).AddMinutes(1)
    while ((Get-Tracker) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if (Get-Tracker) { throw "Le tracker ne s’est pas arrêté." }
    Start-Process -FilePath $Executable -ArgumentList '--background'
}
function Wait-Until([scriptblock]$Condition, [string]$Failure) {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (& $Condition)) {
        if ((Get-Date) -gt $deadline) { throw $Failure }
        Start-Sleep -Seconds 5
    }
}

if ((Get-InstalledVersion) -eq $Version) { Write-Output "Version $Version déjà installée."; return }
$accounts = Get-AccountIds
Restart-Tracker
Wait-Until {
    Get-ChildItem -LiteralPath (Join-Path $data 'updates') -Filter 'prepared-update-*.json' -ErrorAction SilentlyContinue |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).release.version -eq $Version }
} "Version $Version non préparée après $TimeoutMinutes min : vérifier la Release, le canal et « Télécharger automatiquement les mises à jour » dans Réglages → Application."
Restart-Tracker
Wait-Until { (Get-InstalledVersion) -eq $Version -and (Get-Tracker) } "Version $Version non installée : vérifier « Installer au prochain démarrage du tracker » dans Réglages → Application."
$result = Get-Content -LiteralPath (Join-Path $data 'updates/last-result.json') -Raw | ConvertFrom-Json
if (-not $result.Success) { throw "Mise à jour signalée en échec : $($result.Message)" }
$missing = @($accounts | Where-Object { $_ -notin (Get-AccountIds) })
if ($missing) { throw "$($missing.Count) compte(s) absent(s) après la mise à jour." }
Write-Output "Version $Version installée ; $($accounts.Count) compte(s) conservé(s)."
