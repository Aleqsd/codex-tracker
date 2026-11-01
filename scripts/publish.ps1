param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version,
    [string]$Dotnet,
    # "Desktop app" OAuth client JSON embedded for one-click Google Calendar; private, never committed.
    [string]$GoogleClient = (Join-Path $HOME '.codex-tracker\google-oauth-client.json')
)
$ErrorActionPreference = 'Stop'
if (-not $Dotnet) { $Dotnet = & (Join-Path $PSScriptRoot 'find-dotnet.ps1') }
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PSBoundParameters.ContainsKey('Version')) {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $declaredVersion = [string]$buildProperties.Project.PropertyGroup.Version
    if ($declaredVersion -notmatch '^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$') {
        throw 'La version de Directory.Build.props est absente ou invalide.'
    }
    $Version = $declaredVersion
}
$publishRoot = Join-Path $repoRoot "artifacts/publish/$Version"
$clientArguments = @()
if ($GoogleClient -and (Test-Path -LiteralPath $GoogleClient)) {
    $client = Get-Content -LiteralPath $GoogleClient -Raw | ConvertFrom-Json
    if (-not $client.installed.client_id) { throw "Client Google invalide : $GoogleClient (type « Application de bureau » attendu)." }
    $clientArguments = @("-p:GoogleOAuthClientFile=$((Resolve-Path -LiteralPath $GoogleClient).Path)")
    Write-Host "Client Google Agenda intégré : projet $($client.installed.project_id)."
} else {
    Write-Host "Aucun client Google Agenda intégré ($GoogleClient absent) : chaque utilisateur importera le sien." -ForegroundColor Yellow
}
& $Dotnet publish (Join-Path $repoRoot 'src/CodexTracker.App/CodexTracker.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version @clientArguments -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'La publication .NET a échoué.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'README.en.md'), (Join-Path $repoRoot 'AGENTS.md'), (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishRoot
New-Item -ItemType Directory -Path (Join-Path $publishRoot 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/licenses') -Destination (Join-Path $publishRoot 'docs') -Recurse -Force
# Every guide and the images they show, so that the README and guides read correctly from the ZIP.
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File | Where-Object { $_.Extension -in '.md', '.png', '.gif' -and $_.Name -ne 'social-preview.png' } |
    Copy-Item -Destination (Join-Path $publishRoot 'docs')
$validationDirectory = Join-Path $publishRoot 'docs/validation'
New-Item -ItemType Directory -Path $validationDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs/validation') -Filter '*.json' -File | Copy-Item -Destination $validationDirectory
$zipPath = Join-Path $repoRoot "artifacts/CodexTracker-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii
Write-Output $zipPath
