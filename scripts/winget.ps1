#requires -Version 7.0
<#
.SYNOPSIS
Writes the winget manifests of a published release into artifacts/winget, validates them, and with -Submit updates the
pull request in microsoft/winget-pkgs (branch of the Aleqsd/winget-pkgs fork) through the GitHub API.
.DESCRIPTION
The installer hash is the SHA-256 that GitHub computes for the release asset (local artifacts/ as a fallback).
-Submit replaces the manifests of the branch
with this version's, so the open pull request always proposes the latest release. Needs gh (gh auth login).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Submit,
    [string]$Fork = 'Aleqsd/winget-pkgs',
    [string]$Branch = 'codex-tracker-0.8.0',
    [int]$PullRequest = 438574
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) { [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw; $Version = [string]$props.Project.PropertyGroup.Version }
$schema = '1.28.0'
$download = "https://github.com/Aleqsd/codex-tracker/releases/download/v$Version"
$digest = gh api "repos/Aleqsd/codex-tracker/releases/tags/v$Version" --jq ".assets[] | select(.name == \"CodexTracker-$Version-Setup.exe\") | .digest" 2>$null
$hash = if ($digest -match '^sha256:([0-9a-f]{64})$') { $Matches[1] }
if (-not $hash) {
    $local = Join-Path $root "artifacts/CodexTracker-$Version-Setup.exe.sha256"
    if (-not (Test-Path $local)) { throw "Empreinte de l’installateur $Version introuvable (release publiée ou artifacts/)." }
    $hash = ((Get-Content -LiteralPath $local -Raw) -split '\s+')[0]
}
$hash = $hash.ToUpperInvariant()
if ($hash -notmatch '^[0-9A-F]{64}$') { throw "Empreinte invalide : $hash" }

$relative = "manifests/a/Aleqsd/CodexTracker/$Version"
$folder = Join-Path $root "artifacts/winget/$relative"
New-Item -ItemType Directory -Force $folder | Out-Null
$files = [ordered]@{
    'Aleqsd.CodexTracker.yaml' = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: Aleqsd.CodexTracker
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@
    'Aleqsd.CodexTracker.installer.yaml' = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: Aleqsd.CodexTracker
PackageVersion: $Version
InstallerType: inno
Scope: user
MinimumOSVersion: 10.0.22000.0
InstallerSwitches:
  Custom: /CURRENTUSER
UpgradeBehavior: install
ReleaseDate: $((gh release view "v$Version" --repo Aleqsd/codex-tracker --json publishedAt --jq '.publishedAt' 2>$null) -replace 'T.*$', '')
AppsAndFeaturesEntries:
- DisplayName: Codex Tracker
  Publisher: Aleqsd
  ProductCode: '{C84B03F8-B16D-4A0C-9249-148C613C3581}_is1'
Installers:
- Architecture: x64
  InstallerUrl: $download/CodexTracker-$Version-Setup.exe
  InstallerSha256: $hash
ManifestType: installer
ManifestVersion: $schema
"@
    'Aleqsd.CodexTracker.locale.en-US.yaml' = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: Aleqsd.CodexTracker
PackageVersion: $Version
PackageLocale: en-US
Publisher: Aleqsd
PublisherUrl: https://github.com/Aleqsd
PublisherSupportUrl: https://github.com/Aleqsd/codex-tracker/issues
PrivacyUrl: https://codex-tracker.zqsdev.com/privacy/
PackageName: Codex Tracker
PackageUrl: https://codex-tracker.zqsdev.com/
License: MIT
LicenseUrl: https://github.com/Aleqsd/codex-tracker/blob/main/LICENSE
ShortDescription: Codex and Claude Code usage limits, reset times and reminders in the Windows taskbar.
Description: |-
  Shows how much OpenAI Codex and Claude Code usage is left for every account: 5-hour and weekly limits,
  exact reset times, banked Codex reset credits, reminders (Windows, phone through ntfy, Gmail, Google Calendar),
  an on-screen pill when running low, and a local MCP server and command line for coding agents.
  Local only, no account. Independent project, not affiliated with OpenAI or Anthropic.
Moniker: codex-tracker
Tags:
- codex
- claude-code
- usage
- quota
- rate-limit
- system-tray
- developer-tools
- mcp
ReleaseNotesUrl: https://github.com/Aleqsd/codex-tracker/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@
    'Aleqsd.CodexTracker.locale.fr-FR.yaml' = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.$schema.schema.json
PackageIdentifier: Aleqsd.CodexTracker
PackageVersion: $Version
PackageLocale: fr-FR
Publisher: Aleqsd
PublisherUrl: https://github.com/Aleqsd
PublisherSupportUrl: https://github.com/Aleqsd/codex-tracker/issues
PrivacyUrl: https://codex-tracker.zqsdev.com/fr/confidentialite/
PackageName: Codex Tracker
PackageUrl: https://codex-tracker.zqsdev.com/fr/
License: MIT
LicenseUrl: https://github.com/Aleqsd/codex-tracker/blob/main/LICENSE
ShortDescription: Quotas Codex et Claude Code, heures de recharge et rappels dans la barre des tâches Windows.
Description: |-
  Affiche ce qu’il reste de Codex et de Claude Code pour chaque compte : limites sur 5 heures et à la semaine,
  heure exacte des resets, resets en réserve, rappels (Windows, téléphone avec ntfy, Gmail, Google Agenda),
  pastille à l’écran quand un compte est presque vide, serveur MCP local et ligne de commande pour les agents.
  Local, sans compte. Projet indépendant, non affilié à OpenAI ni à Anthropic.
Tags:
- codex
- claude-code
- quotas
- barre-des-taches
ReleaseNotesUrl: https://github.com/Aleqsd/codex-tracker/releases/tag/v$Version
ManifestType: locale
ManifestVersion: $schema
"@
}
foreach ($name in $files.Keys) { [IO.File]::WriteAllText((Join-Path $folder $name), $files[$name].Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false)) }
Write-Output "Manifestes winget $Version : $folder"
if (Get-Command winget -ErrorAction SilentlyContinue) {
    winget validate --manifest $folder --disable-interactivity
    if ($LASTEXITCODE) { throw 'Validation winget échouée.' }
}

if ($Submit) {
    # One commit on the fork branch: this version's files in, every other Codex Tracker version out.
    $head = gh api "repos/$Fork/git/ref/heads/$Branch" --jq '.object.sha'
    $baseTree = gh api "repos/$Fork/git/commits/$head" --jq '.tree.sha'
    $existing = @(gh api "repos/$Fork/git/trees/$($head)?recursive=1" --jq '.tree[] | select(.type=="blob") | .path' | Where-Object { $_ -like 'manifests/a/Aleqsd/CodexTracker/*' })
    $entries = @($existing | Where-Object { -not $_.StartsWith("$relative/") } | ForEach-Object { @{ path = $_; mode = '100644'; type = 'blob'; sha = $null } })
    foreach ($name in $files.Keys) {
        $content = [IO.File]::ReadAllText((Join-Path $folder $name))
        $entries += @{ path = "$relative/$name"; mode = '100644'; type = 'blob'; content = $content }
    }
    $treeBody = @{ base_tree = $baseTree; tree = $entries } | ConvertTo-Json -Depth 5 -Compress
    $tree = $treeBody | gh api "repos/$Fork/git/trees" --input - --jq '.sha'
    $commitBody = @{ message = "Aleqsd.CodexTracker $Version"; tree = $tree; parents = @($head) } | ConvertTo-Json -Compress
    $commit = $commitBody | gh api "repos/$Fork/git/commits" --input - --jq '.sha'
    @{ sha = $commit; force = $false } | ConvertTo-Json -Compress | gh api -X PATCH "repos/$Fork/git/refs/heads/$Branch" --input - | Out-Null
    # REST, not "gh pr edit": its GraphQL query fails on retired classic projects.
    gh api -X PATCH "repos/microsoft/winget-pkgs/pulls/$PullRequest" -f title="New package: Aleqsd.CodexTracker version $Version" | Out-Null
    Write-Output "Demande microsoft/winget-pkgs#$PullRequest mise à jour : $Version (commit $commit)."
}
