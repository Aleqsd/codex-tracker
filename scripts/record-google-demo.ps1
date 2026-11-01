#requires -Version 7.0
<#
.SYNOPSIS
Records the Google OAuth verification video: fictional accounts, real Google connection.
.DESCRIPTION
Builds the tracker with the published OAuth client (%USERPROFILE%\.codex-tracker\google-oauth-client.json), starts
ffmpeg on the primary screen, then opens `--demo --demo-live-google` in English: the dashboard shows fictional accounts,
while Google Calendar and Gmail use the maintainer's real connection, stored in the demo's temporary folder only
(never in the tracker's real data). Nothing is added to the calendar automatically. Follow the shot list printed below,
then press Enter: the video lands in artifacts/google-verification/.
Never run it while Final Fantasy XIV is open; the window takes the focus.
#>
[CmdletBinding()]
param(
    [string]$Dotnet,
    [string]$Ffmpeg = 'ffmpeg',
    [string]$Client = (Join-Path $env:USERPROFILE '.codex-tracker\google-oauth-client.json'),
    # Recorded screen: primary, left, right or DISPLAYn. Keep the tracker and the browser on it.
    [ValidatePattern('^(primary|left|right|DISPLAY\d+)$')][string]$Screen = 'primary'
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name ffxiv_dx11,ffxiv -ErrorAction SilentlyContinue) { throw 'Final Fantasy XIV est en cours : enregistrement reporté.' }
if (-not (Test-Path -LiteralPath $Client)) { throw "Client OAuth publié introuvable : $Client" }
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dotnet) { $Dotnet = & (Join-Path $PSScriptRoot 'find-dotnet.ps1') }
$env:DOTNET_ROOT = Split-Path -Parent (Get-Command $Dotnet).Source
& $Dotnet build (Join-Path $root 'src/CodexTracker.App/CodexTracker.App.csproj') -c Release "-p:GoogleOAuthClientFile=$Client"
if ($LASTEXITCODE) { throw 'Compilation échouée.' }
$exe = Join-Path $root 'src/CodexTracker.App/bin/Release/net10.0-windows10.0.19041.0/CodexTracker.exe'
$out = Join-Path $root 'artifacts/google-verification'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$video = Join-Path $out ("demo-{0:yyyyMMdd-HHmmss}.mp4" -f (Get-Date))

# Physical pixels, as gdigrab captures them.
Add-Type -Namespace RecordDemo -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][RecordDemo.Dpi]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms
$screens = [System.Windows.Forms.Screen]::AllScreens
$primary = $screens | Where-Object Primary
$target = switch -Regex ($Screen) {
    '^primary$' { $primary }
    '^left$' { $screens | Where-Object { $_.Bounds.Right -le $primary.Bounds.Left } | Sort-Object { $_.Bounds.Left } -Descending | Select-Object -First 1 }
    '^right$' { $screens | Where-Object { $_.Bounds.Left -ge $primary.Bounds.Right } | Sort-Object { $_.Bounds.Left } | Select-Object -First 1 }
    default { $screens | Where-Object { $_.DeviceName -eq "\\.\$Screen" } }
}
if (-not $target) { throw "Écran introuvable : $Screen" }
$b = $target.Bounds
Write-Host "Écran enregistré : $($target.DeviceName) $($b.Width)x$($b.Height) à ($($b.X),$($b.Y))"

$recorder = [System.Diagnostics.Process]::new()
$recorder.StartInfo = [System.Diagnostics.ProcessStartInfo]::new($Ffmpeg)
foreach ($argument in '-hide_banner', '-loglevel', 'error', '-f', 'gdigrab', '-framerate', '30', '-draw_mouse', '1',
    '-offset_x', "$($b.X)", '-offset_y', "$($b.Y)", '-video_size', "$($b.Width)x$($b.Height)", '-i', 'desktop',
    '-vf', 'scale=trunc(iw/2)*2:trunc(ih/2)*2', '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23', '-pix_fmt', 'yuv420p', $video) {
    $recorder.StartInfo.ArgumentList.Add($argument)
}
$recorder.StartInfo.RedirectStandardInput = $true
$recorder.StartInfo.UseShellExecute = $false
[void]$recorder.Start()
$tracker = Start-Process -FilePath $exe -ArgumentList '--demo', '--demo-claude', '--demo-resets', '--demo-live-google', '--language', 'en' -PassThru
@'
Recording. Shot list (about 3 minutes):
 1. Dashboard: fictional Codex and Claude Code accounts, then the Resets tab with the banked reset credits.
 2. Settings > Google Calendar > Connect Google Calendar, with "Also send email alerts" checked.
 3. In the browser: show the address bar (client_id), choose the account, tick Calendar and Gmail, Continue.
 4. Back in the tracker: Connected. Resets > a credit > Add to Google Calendar.
 5. Open Google Calendar on that day: the event and its reminders. Delete the event there.
 6. Settings > Google Calendar > Send a test email, then open it in Gmail.
 7. Disconnect: access revoked (also visible at myaccount.google.com/permissions).
Press Enter to stop the recording.
'@ | Write-Host
[void](Read-Host)
$recorder.StandardInput.Write('q'); $recorder.StandardInput.Close()
if (-not $recorder.WaitForExit(30000)) { $recorder.Kill() }
if (-not $tracker.HasExited) { Stop-Process -Id $tracker.Id }
if (-not (Test-Path -LiteralPath $video)) { throw 'Vidéo absente.' }
Write-Output $video
