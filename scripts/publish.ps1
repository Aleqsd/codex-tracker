param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version = '0.4.3',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot "artifacts/publish/$Version"
& $Dotnet publish (Join-Path $repoRoot 'src/CodexTracker.App/CodexTracker.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'La publication .NET a échoué.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'LICENSE') -Destination $publishRoot
New-Item -ItemType Directory -Path (Join-Path $publishRoot 'docs') -Force | Out-Null
foreach ($document in @('dashboard.png', 'dashboard-light.png', 'advice.png', 'history.png', 'VALIDATION.md', 'tray-minimal.png')) {
    $source = Join-Path $repoRoot "docs/$document"
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $publishRoot "docs/$document") }
}
$zipPath = Join-Path $repoRoot "artifacts/CodexTracker-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zipPath.sha256" -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii
Write-Output $zipPath
