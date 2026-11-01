#requires -Version 7.0
<#
.SYNOPSIS
Builds the public site (codex-tracker.zqsdev.com) into artifacts/site, and publishes it with -Deploy.
.DESCRIPTION
The build itself is scripts/build-site.py, the same one Netlify runs on every push to main (netlify.toml):
pushing is enough to publish. -Deploy publishes the local build right away through the Netlify CLI
(`netlify login` needed), for example to check a change before pushing it; -Draft gives a preview URL instead.
#>
[CmdletBinding()]
param(
    [switch]$Deploy,
    [switch]$Draft,
    [string]$Python = 'python',
    [string]$Site = '4614a772-0a46-40bd-a0d3-85f95dd514b7' # Netlify project codex-tracker-zqsdev
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts/site'
$production = $Deploy -and -not $Draft
& $Python (Join-Path $root 'scripts/build-site.py') @(if ($production) { '--indexnow' })
if ($LASTEXITCODE) { throw 'Construction du site échouée.' }
if ($Deploy -or $Draft) {
    $netlify = (Get-Command netlify -ErrorAction Stop).Source
    $arguments = @('deploy', '--dir', $out, '--site', $Site, '--message', 'Déploiement local')
    if ($production) { $arguments += '--prod' }
    & $netlify @arguments
    if ($LASTEXITCODE) { throw 'Déploiement Netlify échoué.' }
}
