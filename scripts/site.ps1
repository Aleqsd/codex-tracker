#requires -Version 7.0
<#
.SYNOPSIS
Builds the public site (codex-tracker.zqsdev.com) into artifacts/site, and publishes it with -Deploy.
.DESCRIPTION
The build itself is scripts/build-site.py, the same one Cloudflare Workers Builds runs on every push to main
(wrangler.jsonc): pushing is enough to publish. -Deploy publishes the local build right away with `npx wrangler deploy`
(`npx wrangler login` needed), for example after a release; -Draft serves it locally with `wrangler dev` instead.
#>
[CmdletBinding()]
param(
    [switch]$Deploy,
    [switch]$Draft,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts/site'
$production = $Deploy -and -not $Draft
& $Python (Join-Path $root 'scripts/build-site.py') @(if ($production) { '--indexnow' })
if ($LASTEXITCODE) { throw 'Construction du site échouée.' }
if ($Deploy -or $Draft) {
    Push-Location $root
    try { & npx --yes wrangler $(if ($production) { 'deploy' } else { 'dev' }) }
    finally { Pop-Location }
    if ($LASTEXITCODE) { throw 'Déploiement Cloudflare échoué.' }
}
