#requires -Version 7.0
<#
.SYNOPSIS
Moves the repository to a new version: Directory.Build.props and the README download links, then restores.
.DESCRIPTION
Commit the changed files together; a changed packages.lock.json means a real dependency change.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version,
    [string]$Dotnet
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dotnet) { $Dotnet = & (Join-Path $PSScriptRoot 'find-dotnet.ps1') }
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $Dotnet).Source
$props = Join-Path $root 'Directory.Build.props'
[xml]$buildProperties = Get-Content -LiteralPath $props -Raw
$previous = [string]$buildProperties.Project.PropertyGroup.Version
if ($previous -eq $Version) { throw "La version est déjà $Version." }
foreach ($file in @($props, (Join-Path $root 'README.md'))) {
    $text = [IO.File]::ReadAllText($file)
    $updated = $text.Replace($previous, $Version)
    if ($updated -eq $text) { throw "Version $previous absente de $file." }
    [IO.File]::WriteAllText($file, $updated)
}
& $Dotnet restore (Join-Path $root 'CodexTracker.slnx')
if ($LASTEXITCODE) { throw 'Restauration échouée.' }
Write-Output "Version $previous → $Version : fichiers modifiés à committer ensemble."
