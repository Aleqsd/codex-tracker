#requires -Version 7.0
<#
.SYNOPSIS
Local release pipeline: checks, packages, published-binary tests, then the GitHub Release and this PC's update.
.DESCRIPTION
Without -Publish, builds and verifies the artifacts under artifacts/ only.
-Publish requires a clean tree, the commit pushed to origin/main, README links to this version
and a notes file holding only the feature bullets: download links and the ZIP checksum are added here.
Refuses to start while Final Fantasy XIV runs: the WPF suite activates real windows.
The installer lifecycle needs a disposable Windows profile and is not run on this PC (docs/PUBLISHED-UPDATE-TEST.md).
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version,
    [string]$Dotnet = 'dotnet',
    [string]$Python = 'python',
    [switch]$InstallCompiler,
    [switch]$Publish,
    [string]$NotesPath,
    [switch]$UpdateThisPc
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
# Framework-dependent test hosts need the SDK's runtime when it is not installed system-wide.
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $Dotnet).Source

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
    Invoke-Step 'Compilation' { & $Dotnet build CodexTracker.slnx -c Release --no-restore }
    Invoke-Step 'Gardes de signature' { & ./scripts/test-release-signing.ps1 }
    Invoke-Step 'Manifeste WinGet' { & ./scripts/test-winget.ps1 }
    Invoke-Step 'Tests métier' { & $Dotnet test tests/CodexTracker.Tests/CodexTracker.Tests.csproj -c Release --no-build }
    Invoke-Step 'Suite WPF (données fictives)' { & $Dotnet run --project tests/CodexTracker.UiSmoke/CodexTracker.UiSmoke.csproj -c Release --no-build }
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
    Invoke-Step 'Collecteurs Claude Code publiés' { & $Python scripts/test-claude.py $exe }
    Invoke-Step 'Délai de démarrage MCP' { & ./scripts/test-mcp-startup.ps1 -ExecutablePath $exe }
    $report = [ordered]@{ version = $Version; commit = $commit; uncommittedChanges = $uncommitted; builtAt = [DateTimeOffset]::UtcNow.ToString('o')
        durationSeconds = [int]$watch.Elapsed.TotalSeconds; sha256 = $hashes; published = $false
        installerLifecycle = 'not run: requires a disposable Windows profile' }

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
    }
    $report | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $root "artifacts/release-$Version.json") -Encoding utf8
    if ($UpdateThisPc) { Invoke-Step 'Mise à jour de ce PC' { & ./scripts/update-this-pc.ps1 -Version $Version } }
    Write-Host "Release $Version prête en $([int]$watch.Elapsed.TotalMinutes) min : $setup" -ForegroundColor Green
} finally {
    if ($cleanLocks) { & git -C $root checkout -- @cleanLocks }
    Pop-Location
}
