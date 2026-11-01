# Codex Tracker installer for Windows 11: irm https://codex-tracker.zqsdev.com/install.ps1 | iex
# Downloads the latest release from GitHub, checks its SHA-256, installs it for the current user (no admin rights)
# and starts it in the notification area. Source: https://github.com/Aleqsd/codex-tracker
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Say([string]$text) { Write-Host "Codex Tracker: $text" }
if ([Environment]::OSVersion.Platform -ne 'Win32NT' -or [Environment]::OSVersion.Version.Build -lt 22000) { throw 'Codex Tracker needs Windows 11.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Codex Tracker needs 64-bit Windows.' }

$release = Invoke-RestMethod 'https://api.github.com/repos/Aleqsd/codex-tracker/releases/latest' -Headers @{ 'User-Agent' = 'codex-tracker-install' }
$version = $release.tag_name.TrimStart('v')
$installed = Join-Path $env:LOCALAPPDATA 'Programs\CodexTracker\CodexTracker.exe'
if (Test-Path $installed) {
    $current = (Get-Item $installed).VersionInfo.ProductVersion -replace '\+.*$', ''
    if ($current -and [version]($current -replace '-.*$', '') -ge [version]$version) {
        Say "version $current is already installed; it updates itself."
        Start-Process $installed -ArgumentList '--background'
        return
    }
}

$setup = $release.assets | Where-Object name -eq "CodexTracker-$version-Setup.exe"
if (-not $setup) { throw "No installer found in release $version." }
$folder = Join-Path ([IO.Path]::GetTempPath()) ("codex-tracker-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
try {
    $file = Join-Path $folder $setup.name
    Say "downloading $version..."
    Invoke-WebRequest $setup.browser_download_url -OutFile $file -UseBasicParsing
    # GitHub computes each release asset's SHA-256 on upload.
    $expected = if ($setup.digest -match '^sha256:([0-9a-f]{64})$') { $Matches[1] }
    if (-not $expected) { throw 'No published SHA-256 for this installer: nothing was installed.' }
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw 'The download does not match the published SHA-256: nothing was installed.' }
    # A running tracker is closed first so that its files can be replaced; its data and settings are kept.
    if (Test-Path $installed) {
        Start-Process $installed -ArgumentList '--exit' -Wait -ErrorAction SilentlyContinue
        Get-Process CodexTracker -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installed } | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    }
    Say 'installing for the current user...'
    $process = Start-Process $file -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "The installer stopped with code $($process.ExitCode)." }
} finally { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }

if (-not (Test-Path $installed)) { throw 'Installation finished but CodexTracker.exe was not found.' }
Start-Process $installed -ArgumentList '--background'
Say "version $version installed and running in the notification area (click the ^ arrow next to the clock to pin it)."
Say 'for coding agents: turn on Settings > Assistants in the app, then use "CodexTracker.exe --mcp" as an MCP server or "CodexTracker.exe --cli help".'
