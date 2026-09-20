param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version = '0.2.1',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot "artifacts/publish/$Version"
& $Dotnet publish (Join-Path $repoRoot 'src/CodexTracker.App/CodexTracker.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'La publication .NET a échoué.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'LICENSE') -Destination $publishRoot
if (Test-Path -LiteralPath (Join-Path $repoRoot 'docs/dashboard.png')) {
    New-Item -ItemType Directory -Path (Join-Path $publishRoot 'docs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/dashboard.png') -Destination (Join-Path $publishRoot 'docs/dashboard.png')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/VALIDATION.md') -Destination (Join-Path $publishRoot 'docs/VALIDATION.md')
    if (Test-Path -LiteralPath (Join-Path $repoRoot 'docs/tray-ring.png')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/tray-ring.png') -Destination (Join-Path $publishRoot 'docs/tray-ring.png')
    }
}
$zipPath = Join-Path $repoRoot "artifacts/CodexTracker-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii
Write-Output $zipPath
