param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.8.2'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $repoRoot "artifacts/CodexTracker-$Version-Setup.exe"
if (!(Test-Path -LiteralPath $installer)) { throw "Construisez l’installateur de cette version avant le manifeste." }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
$checksum = (Get-Content -LiteralPath "$installer.sha256").Split(' ')[0]
if ($hash -ne $checksum) { throw "Le SHA-256 de l’installateur diffère du fichier publié." }
$folder = Join-Path $repoRoot "artifacts/winget/manifests/a/Aleqsd/CodexTracker/$Version"
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$url = "https://github.com/Aleqsd/codex-tracker/releases/download/v$Version/CodexTracker-$Version-Setup.exe"
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
PackageIdentifier: Aleqsd.CodexTracker
PackageVersion: $Version
DefaultLocale: fr-FR
ManifestType: version
ManifestVersion: 1.12.0
"@ | Set-Content -LiteralPath (Join-Path $folder 'Aleqsd.CodexTracker.yaml') -Encoding utf8
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
PackageIdentifier: Aleqsd.CodexTracker
PackageVersion: $Version
PackageLocale: fr-FR
Publisher: Aleqsd
PublisherUrl: https://github.com/Aleqsd
PublisherSupportUrl: https://github.com/Aleqsd/codex-tracker/issues
PrivacyUrl: https://github.com/Aleqsd/codex-tracker/blob/main/docs/PRIVACY.md
PackageName: Codex Tracker
PackageUrl: https://github.com/Aleqsd/codex-tracker
License: MIT
LicenseUrl: https://github.com/Aleqsd/codex-tracker/blob/main/LICENSE
ShortDescription: Suivi local des quotas et resets Codex dans la barre des tâches Windows.
Description: |-
  Suivi des comptes détectés dans Codex, quotas, agenda des resets et réserves.
  Rappels Windows locaux et connecteurs Twilio/SendGrid facultatifs.
  Interface française, thèmes clair/sombre et MCP local désactivé par défaut.
  Nécessite Windows 11 et Codex avec sa CLI. Application indépendante d’OpenAI.
Moniker: codex-tracker
Tags:
- codex
- notifications
- quotas
- system-tray
ReleaseNotesUrl: https://github.com/Aleqsd/codex-tracker/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.12.0
"@ | Set-Content -LiteralPath (Join-Path $folder 'Aleqsd.CodexTracker.locale.fr-FR.yaml') -Encoding utf8
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
PackageIdentifier: Aleqsd.CodexTracker
PackageVersion: $Version
InstallerType: inno
Scope: user
MinimumOSVersion: 10.0.22000.0
InstallerSwitches:
  Custom: /CURRENTUSER
UpgradeBehavior: install
AppsAndFeaturesEntries:
- DisplayName: Codex Tracker
  Publisher: Aleqsd
  ProductCode: '{C84B03F8-B16D-4A0C-9249-148C613C3581}_is1'
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $hash
ManifestType: installer
ManifestVersion: 1.12.0
"@ | Set-Content -LiteralPath (Join-Path $folder 'Aleqsd.CodexTracker.installer.yaml') -Encoding utf8
& winget validate --manifest $folder --disable-interactivity
if ($LASTEXITCODE) { throw 'Validation WinGet échouée.' }
Write-Output $folder
