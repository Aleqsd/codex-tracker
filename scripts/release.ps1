#requires -Version 7.0
<#
.SYNOPSIS
Local release pipeline: checks, packages, published-binary tests, then the GitHub Release and this PC's update.
.DESCRIPTION
Without -Publish, builds and verifies the artifacts under artifacts/ only.
-Publish requires a clean tree, the commit pushed to origin/main, README links to this version
and a notes file holding only the feature bullets: download links and the ZIP checksum are added here.
Refuses to start while Final Fantasy XIV runs: the WPF suite activates real windows.
-Quick skips the checks that the changes since the last release tag cannot affect: the WPF suite unless a view,
the tray or XAML changed, the Claude collectors unless the Codex project changed, the MCP start delay unless Mcp/ changed.
A change to the site, docs or scripts alone needs no release: Cloudflare Workers Builds rebuilds the site on push.
After publishing, the winget manifests (artifacts/winget) and the Store package (artifacts/*.msix) of the version are
prepared; submitting them stays explicit (scripts/winget.ps1 -Submit, Partner Center).
The installer lifecycle needs a disposable Windows profile and is not run on this PC (docs/PUBLISHED-UPDATE-TEST.md).
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version,
    [string]$Dotnet,
    [string]$Python = 'python',
    [switch]$InstallCompiler,
    [switch]$Publish,
    [string]$NotesPath,
    [switch]$UpdateThisPc,
    [switch]$Quick,
    # Screen for the real WPF test windows: right (default), left, primary or DISPLAYn.
    [ValidatePattern('^(right|left|primary|DISPLAY\d+)$')]
    [string]$TestScreen
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name ffxiv_dx11,ffxiv -ErrorAction SilentlyContinue) {
    throw 'Final Fantasy XIV est en cours : la suite WPF activerait de vraies fenêtres. Relancer la release après la session de jeu.'
}
$root = Split-Path -Parent $PSScriptRoot
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$declaredVersion = [string]$buildProperties.Project.PropertyGroup.Version
if (-not $Version) { $Version = $declaredVersion }
if ($Version -ne $declaredVersion) { throw "Directory.Build.props déclare $declaredVersion, pas $Version." }
if ($UpdateThisPc -and -not $Publish) { throw '-UpdateThisPc installe la Release publiée : ajouter -Publish.' }
if (-not $Dotnet) { $Dotnet = & (Join-Path $PSScriptRoot 'find-dotnet.ps1') }
# Framework-dependent test hosts need the SDK's runtime when it is not installed system-wide.
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $Dotnet).Source
if ($TestScreen) { $env:CODEX_TRACKER_TEST_SCREEN = $TestScreen }

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Action
    if ($LASTEXITCODE) { throw "$Name : échec (code $LASTEXITCODE)." }
}
function Get-Git([string[]]$Arguments) {
    $output = & git -C $root @Arguments
    if ($LASTEXITCODE) { throw "git $($Arguments -join ' ') a échoué." }
    $output
}

$commit = Get-Git @('rev-parse', 'HEAD')
$uncommitted = [bool](Get-Git @('status', '--porcelain'))
if ($Publish) {
    if (-not $NotesPath -or -not (Test-Path -LiteralPath $NotesPath)) { throw 'Indiquer -NotesPath : un fichier avec les puces de nouveautés.' }
    & gh auth status *> $null
    if ($LASTEXITCODE) { throw "gh n’est pas connecté : lancer gh auth login." }
    if (Get-Git @('status', '--porcelain')) { throw 'Arbre de travail modifié : committer ou annuler avant de publier.' }
    Get-Git @('fetch', '--quiet', 'origin', 'main') | Out-Null
    if ((Get-Git @('rev-parse', 'origin/main')) -ne $commit) { throw "HEAD ($commit) n’est pas origin/main : pousser le commit à publier d’abord." }
    & gh release view "v$Version" *> $null
    if (-not $LASTEXITCODE) { throw "La Release v$Version existe déjà." }
    if (-not (Select-String -LiteralPath (Join-Path $root 'README.md') -SimpleMatch "releases/download/v$Version/" -Quiet)) {
        throw "README.md ne pointe pas encore vers v$Version."
    }
}
# -Quick: what changed since the last published tag decides which slow checks still run.
$changed = @()
$runUi = $runCollectors = $runStartDelay = $true
if ($Quick) {
    & git -C $root fetch --tags --quiet origin 2>$null
    $lastTag = (& git -C $root describe --tags --abbrev=0 --match 'v*' HEAD~1 2>$null)
    if ($LASTEXITCODE -or -not $lastTag) { Write-Host 'Aucun tag précédent : contrôles complets.' -ForegroundColor Yellow }
    else {
        $changed = @(Get-Git @('diff', '--name-only', $lastTag, 'HEAD'))
        $runUi = [bool]($changed | Where-Object { $_ -match '^(src/CodexTracker\.App/.*(\.xaml|View[^/]*\.cs|Tray[^/]*\.cs|Ui[^/]*\.cs|Window[^/]*\.cs|Theme[^/]*\.cs)|tests/CodexTracker\.UiSmoke/)' })
        $runCollectors = [bool]($changed | Where-Object { $_ -match '^(src/CodexTracker\.Codex/|scripts/test-claude\.py)' })
        $runStartDelay = [bool]($changed | Where-Object { $_ -match '^(src/CodexTracker\.App/(Mcp/|EntryPoint\.cs)|scripts/test-mcp-startup\.ps1)' })
        Write-Host ("Rapide depuis {0} : WPF {1}, collecteurs Claude {2}, délai MCP {3}." -f $lastTag,
            ($runUi ? 'oui' : 'non'), ($runCollectors ? 'oui' : 'non'), ($runStartDelay ? 'oui' : 'non')) -ForegroundColor Yellow
    }
}
# Restores rewrite lock files with SDK-specific sections; put back only those that were clean beforehand.
$cleanLocks = @(Get-Git @('ls-files', '--', '*packages.lock.json') | Where-Object { -not (Get-Git @('status', '--porcelain', '--', $_)) })

Push-Location $root
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Step 'Syntaxe des scripts PowerShell' {
        Get-ChildItem scripts/*.ps1 | ForEach-Object {
            $tokens = $null; $errors = $null
            $null = [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
        }
    }
    Invoke-Step 'Restauration' { & $Dotnet restore CodexTracker.slnx }
    # Lock files record project versions: a release must ship the ones it was restored with.
    if ($Publish -and (Get-Git @('status', '--porcelain', '--', '*packages.lock.json'))) {
        throw 'packages.lock.json obsolètes : lancer scripts/bump.ps1 ou committer la restauration.'
    }
    Invoke-Step 'Compilation' { & $Dotnet build CodexTracker.slnx -c Release --no-restore }
    Invoke-Step 'Tests métier' { & $Dotnet test tests/CodexTracker.Tests/CodexTracker.Tests.csproj -c Release --no-build }
    if ($runUi) { Invoke-Step 'Suite WPF (données fictives)' { & $Dotnet run --project tests/CodexTracker.UiSmoke/CodexTracker.UiSmoke.csproj -c Release --no-build } }
    Invoke-Step 'ZIP portable' { & ./scripts/publish.ps1 -Version $Version -Dotnet $Dotnet }
    Invoke-Step 'Installateur' { & ./scripts/build-installer.ps1 -Version $Version -InstallCompiler:$InstallCompiler }
    $setup = Join-Path $root "artifacts/CodexTracker-$Version-Setup.exe"
    $zip = Join-Path $root "artifacts/CodexTracker-$Version-win-x64.zip"
    $hashes = [ordered]@{}
    foreach ($file in @($setup, $zip)) {
        $expected = ((Get-Content -LiteralPath "$file.sha256" -Raw) -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -ne $actual) { throw "Empreinte incohérente : $file" }
        $hashes[(Split-Path -Leaf $file)] = $actual
    }
    $exe = Join-Path $root "artifacts/publish/$Version/CodexTracker.exe"
    Invoke-Step "MCP stdio sur l’exécutable publié" { & $Python scripts/test-mcp.py $exe }
    if ($runCollectors) { Invoke-Step 'Collecteurs Claude Code publiés' { & $Python scripts/test-claude.py $exe } }
    if ($runStartDelay) { Invoke-Step 'Délai de démarrage MCP' { & ./scripts/test-mcp-startup.ps1 -ExecutablePath $exe } }
    $report = [ordered]@{ version = $Version; commit = $commit; uncommittedChanges = $uncommitted; builtAt = [DateTimeOffset]::UtcNow.ToString('o')
        durationSeconds = [int]$watch.Elapsed.TotalSeconds; sha256 = $hashes; published = $false
        installerLifecycle = 'not run: requires a disposable Windows profile'
        skipped = @(if (-not $runUi) { 'wpf' }; if (-not $runCollectors) { 'claude-collectors' }; if (-not $runStartDelay) { 'mcp-start-delay' }) }

    if ($Publish) {
        $notes = Join-Path $root "artifacts/release-notes-$Version.md"
        $download = "https://github.com/Aleqsd/codex-tracker/releases/download/v$Version"
        @("- [Installer Windows recommandé]($download/CodexTracker-$Version-Setup.exe) · [ZIP portable en option]($download/CodexTracker-$Version-win-x64.zip)"
            (Get-Content -LiteralPath $NotesPath -Raw).Trim()
            ''
            "SHA-256 du ZIP : ``$($hashes[(Split-Path -Leaf $zip)])``") | Set-Content -LiteralPath $notes -Encoding utf8
        Invoke-Step "Release GitHub v$Version" {
            & gh release create "v$Version" --target $commit --latest --title "Codex Tracker $Version" --notes-file $notes $setup $zip "$zip.sha256"
        }
        $report.published = $true
        # The site shows the latest published release: rebuild it from main now that this one exists.
        try {
            & (Join-Path $PSScriptRoot 'site.ps1') -Deploy *> $null
            Write-Host 'Site republié sur Cloudflare.' -ForegroundColor Gray
        } catch {
            Write-Host 'Site non republié : relancer scripts/site.ps1 -Deploy.' -ForegroundColor Yellow
        }
        $global:LASTEXITCODE = 0
    }
    $report | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $root "artifacts/release-$Version.json") -Encoding utf8
    if ($Publish) {
        # Distribution files for this version; a failure here never undoes the published release.
        try { & ./scripts/winget.ps1 -Version $Version | Out-Null; Write-Host "Manifestes winget $Version prêts." -ForegroundColor Gray }
        catch { Write-Host "Manifestes winget non préparés : $($_.Exception.Message)" -ForegroundColor Yellow }
        try { & ./scripts/build-msix.ps1 -Version $Version | Out-Null; Write-Host "Paquet Store $Version prêt." -ForegroundColor Gray }
        catch { Write-Host "Paquet Store non préparé : $($_.Exception.Message)" -ForegroundColor Yellow }
        $global:LASTEXITCODE = 0
    }
    if ($UpdateThisPc) { Invoke-Step 'Mise à jour de ce PC' { & ./scripts/update-this-pc.ps1 -Version $Version } }
    Write-Host "Release $Version prête en $([int]$watch.Elapsed.TotalMinutes) min : $setup" -ForegroundColor Green
} finally {
    if ($cleanLocks) { & git -C $root checkout -- @cleanLocks }
    Pop-Location
}
