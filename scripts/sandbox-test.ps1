#requires -Version 7.0
<#
.SYNOPSIS
Installation test on a clean Windows, in Windows Sandbox: installer, update over the previous release, PowerShell one-liner, uninstallation.
.DESCRIPTION
Requires the optional Windows feature « Windows Sandbox » (Windows Pro, enabled once by the user, then a restart).
The local installer of -Version (artifacts/CodexTracker-<version>-Setup.exe) is mapped read-only; the previous release is
downloaded from GitHub inside the sandbox. Results land in artifacts/sandbox/result.txt; the sandbox is discarded on close.
Never run it while Final Fantasy XIV is open: the sandbox window takes the focus.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Previous,
    [int]$TimeoutMinutes = 15
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name ffxiv_dx11,ffxiv -ErrorAction SilentlyContinue) { throw 'Final Fantasy XIV est en cours : test Sandbox reporté.' }
$sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (-not (Test-Path -LiteralPath $sandbox)) { throw 'Windows Sandbox absent : Fonctionnalités facultatives Windows → « Bac à sable Windows », puis redémarrer.' }
$root = Split-Path -Parent $PSScriptRoot
$setup = Join-Path $root "artifacts/CodexTracker-$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "Installateur absent : $setup" }
if (-not $Previous) {
    $Previous = (gh release list --repo Aleqsd/codex-tracker --exclude-pre-releases --limit 10 --json tagName --jq '.[].tagName' |
        ForEach-Object { $_.TrimStart('v') } | Where-Object { [version]$_ -lt [version]$Version } | Select-Object -First 1)
}
$work = Join-Path $root 'artifacts/sandbox'
Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
$inputs = New-Item -ItemType Directory -Force -Path (Join-Path $work 'in')
$results = New-Item -ItemType Directory -Force -Path (Join-Path $work 'out')
Copy-Item $setup (Join-Path $inputs 'Setup.exe')

# Runs inside the sandbox as WDAGUtilityAccount, with Windows PowerShell 5.1.
@"
`$ErrorActionPreference = 'Continue'
`$log = 'C:\Results\result.txt'
function Note([string]`$text) { Add-Content -Path `$log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), `$text) }
function Check([string]`$name, [bool]`$ok) { Note ((@('FAIL','PASS')[[int]`$ok]) + ' ' + `$name) }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
`$exe = Join-Path `$env:LOCALAPPDATA 'Programs\CodexTracker\CodexTracker.exe'
`$silent = '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER'
function Version { if (Test-Path `$exe) { (Get-Item `$exe).VersionInfo.ProductVersion } else { '' } }
function Smoke([string]`$name) {
    `$p = Start-Process `$exe -ArgumentList '--smoke-test' -PassThru
    Check "`$name : démarrage et arrêt propre" (`$p.WaitForExit(60000) -and `$p.ExitCode -eq 0)
}
function Uninstall {
    Get-Process CodexTracker -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
    `$u = Join-Path `$env:LOCALAPPDATA 'Programs\CodexTracker\unins000.exe'
    if (Test-Path `$u) { Start-Process `$u -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait }
    Start-Sleep 3
    Check 'Désinstallation : exécutable retiré' (-not (Test-Path `$exe))
}
Note 'Début'
try {
    # 1. Previous release, then the new installer over it: an update keeps the data folder.
    `$old = Join-Path `$env:TEMP 'Previous-Setup.exe'
    Invoke-WebRequest "https://github.com/Aleqsd/codex-tracker/releases/download/v$Previous/CodexTracker-$Previous-Setup.exe" -OutFile `$old -UseBasicParsing
    Start-Process `$old -ArgumentList `$silent -Wait
    Check "Installation $Previous" ((Version) -like '$Previous*')
    `$data = Join-Path `$env:LOCALAPPDATA 'CodexTracker'
    New-Item -ItemType Directory -Force -Path `$data | Out-Null
    Set-Content -Path (Join-Path `$data 'sandbox-marker.txt') -Value 'keep'
    Start-Process 'C:\Inputs\Setup.exe' -ArgumentList `$silent -Wait
    Check "Mise à jour vers $Version" ((Version) -like '$Version*')
    Check 'Mise à jour : données conservées' (Test-Path (Join-Path `$data 'sandbox-marker.txt'))
    Smoke 'Version mise à jour'
    Uninstall
    Check 'Désinstallation : données conservées' (Test-Path (Join-Path `$data 'sandbox-marker.txt'))
    # 2. Fresh installation with the local installer.
    Start-Process 'C:\Inputs\Setup.exe' -ArgumentList `$silent -Wait
    Check "Installation neuve $Version" ((Version) -like '$Version*')
    Smoke 'Installation neuve'
    Check 'Raccourci du menu Démarrer' ((Get-ChildItem (Join-Path `$env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter 'Codex Tracker*.lnk').Count -gt 0)
    Uninstall
    # 3. The published one-liner (latest GitHub release, SHA-256 checked by the script).
    Invoke-Expression (Invoke-RestMethod 'https://codex-tracker.zqsdev.com/install.ps1' -UseBasicParsing)
    Check 'Installation en une ligne' (Test-Path `$exe)
    Note ('Version installée par la ligne : ' + (Version))
    Uninstall
} catch { Note ('ERREUR ' + `$_.Exception.Message) }
Note 'Fin'
Set-Content -Path 'C:\Results\done.txt' -Value 'done'
"@ | Set-Content -Encoding utf8BOM -Path (Join-Path $inputs 'run.ps1')

$wsb = Join-Path $work 'test.wsb'
@"
<Configuration>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$($inputs.FullName)</HostFolder><SandboxFolder>C:\Inputs</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$($results.FullName)</HostFolder><SandboxFolder>C:\Results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Inputs\run.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content -Encoding UTF8 -Path $wsb

Write-Output "Sandbox : $Previous → $Version, puis installation neuve et ligne PowerShell."
Start-Process $wsb
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path (Join-Path $results 'done.txt')) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
Get-Process WindowsSandboxRemoteSession, WindowsSandboxServer, WindowsSandboxClient, WindowsSandbox -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$result = Join-Path $results 'result.txt'
if (-not (Test-Path $result)) { throw "Aucun résultat : le Sandbox n'a pas lancé le test." }
Get-Content $result
if (Select-String -Path $result -Pattern '^\S+ (FAIL|ERREUR)' -Quiet) { throw 'Test Sandbox échoué.' }
if (-not (Test-Path (Join-Path $results 'done.txt'))) { throw "Test Sandbox inachevé après $TimeoutMinutes min." }
