#requires -Version 7.0
# Isolated fixtures: no network, package installation or real WinGet settings.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path ([IO.Path]::GetTempPath()) ('CodexTrackerWingetTests-' + [guid]::NewGuid().ToString('N'))
$checks = 0
$previousExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$previousExitValue = if ($previousExitCode) { $previousExitCode.Value } else { $null }
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$ExpectedError) {
    try { & $Action; throw 'Expected rejection was absent.' }
    catch { Check ($_.Exception.Message -like "*$ExpectedError*") "Wrong rejection: $($_.Exception.Message)" }
}
function Invoke-WebRequest($Uri, $OutFile, $TimeoutSec) {
    $script:lastUrl = $Uri; $script:downloads++
    if ($script:networkFails) { throw 'Simulated network failure' }
    [IO.File]::WriteAllText($OutFile, $script:publicBytes)
}
function winget {
    $script:validationArguments = @($args)
    $global:LASTEXITCODE = $script:validationExitCode
}
try {
    New-Item -ItemType Directory -Path "$root/scripts", "$root/artifacts" | Out-Null
    Copy-Item -LiteralPath "$PSScriptRoot/winget.ps1" -Destination "$root/scripts/winget.ps1"
    $script:publicBytes = 'fictional installer bytes'
    $script:networkFails = $false; $script:validationExitCode = 0; $script:downloads = 0
    $fixture = "$root/artifacts/CodexTracker-1.2.3-Setup.exe"
    [IO.File]::WriteAllText($fixture, $script:publicBytes)
    Set-Content "$root/Directory.Build.props" '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
    $folder = . "$root/scripts/winget.ps1"
    Check ($folder -like '*CodexTracker/1.2.3' -or $folder -like '*CodexTracker\1.2.3') 'Version must come from build properties.'
    Check (@(Get-ChildItem $folder -File).Count -eq 3) 'Exactly three manifests are required.'
    Check ($script:lastUrl -eq 'https://github.com/Aleqsd/codex-tracker/releases/download/v1.2.3/CodexTracker-1.2.3-Setup.exe') 'Public URL must be version-specific.'
    Check (($script:validationArguments -join ' ') -eq "validate --manifest $folder --disable-interactivity") 'Validate the generated manifest without installing.'
    $manifest = Join-Path $folder 'Aleqsd.CodexTracker.installer.yaml'
    $before = Get-Content $manifest -Raw
    Check ($before.Contains("`r`n") -and !$before.Replace("`r`n", '').Contains("`n")) 'Manifests must use CRLF line endings.'
    Check ($before.Contains((Get-FileHash $fixture -Algorithm SHA256).Hash)) 'Manifest must contain the verified installer hash.'
    $script:publicBytes = 'different public build'
    Reject { . "$root/scripts/winget.ps1" } 'fichier public diffère'
    Check ((Get-Content $manifest -Raw) -eq $before) 'A mismatch must not overwrite the previous manifest.'
    $script:publicBytes = 'fictional installer bytes'; $script:networkFails = $true
    Reject { . "$root/scripts/winget.ps1" } 'Simulated network failure'
    $script:networkFails = $false
    Set-Content "$fixture.sha256" ('0' * 64)
    $countBefore = $script:downloads
    Reject { . "$root/scripts/winget.ps1" } 'checksum local'
    Check ($script:downloads -eq $countBefore) 'A damaged local file must be rejected before download.'
    Remove-Item -LiteralPath "$fixture.sha256"
    Set-Content "$root/Directory.Build.props" '<Project><PropertyGroup><Version>1.3.0-beta.1</Version></PropertyGroup></Project>'
    Reject { . "$root/scripts/winget.ps1" } 'version stable numérique'
    $folder = . "$root/scripts/winget.ps1" -Version 1.2.3 -InstallerPath $fixture
    Check (Test-Path (Join-Path $folder 'Aleqsd.CodexTracker.yaml')) 'An explicit stable version and candidate path must be accepted without a sidecar.'
    $script:validationExitCode = 1
    Reject { . "$root/scripts/winget.ps1" -Version 1.2.3 -InstallerPath $fixture } 'Validation WinGet échouée'
    Write-Output "$checks WinGet preparation checks passed (simulated download and validator)."
}
finally {
    if ($previousExitCode) { $global:LASTEXITCODE = $previousExitValue }
    else { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
    # This exact unique temporary directory was created above; no user profile data.
    if ((Split-Path $root -Parent) -ne [IO.Path]::GetTempPath().TrimEnd('\', '/')) { throw 'Unexpected test directory.' }
    Remove-Item -LiteralPath $root -Recurse -Force
}
