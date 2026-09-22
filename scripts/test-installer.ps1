#requires -Version 7.0
<#
.SYNOPSIS
Tests the per-user installer lifecycle in a fresh, disposable Windows profile.
.DESCRIPTION
Without -Run, prints a plan without files, network requests or child processes.
Never run against a normal user profile. Existing Codex/Tracker data, installations,
processes and reparse points are refused. The harness never deletes application data.
A CI Windows Server administrator run does not prove Windows 11 standard-user behavior.
#>
[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$DedicatedTestProfile,
    [string]$SetupPath = '',
    [string]$ExpectedVersion = '',
    [string]$ReportPath = 'artifacts/installer-lifecycle.json'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (!$Run) {
    [ordered]@{
        mode = 'plan'; writesFiles = $false; usesNetwork = $false; launchesProcesses = $false
        requires = @('Windows x64; PowerShell 7; fresh disposable profile',
            '-Run -DedicatedTestProfile -SetupPath -ExpectedVersion',
            'No Codex home, Tracker data, installation or process; no reparse points')
        steps = @('Install silently for the current user; verify executable and HKCU registration',
            'Start in background without a Codex session, probe the dispatcher, exit cleanly',
            'Create disabled fictional preferences and two accounts; hash fixture files',
            'Reinstall the same setup and verify unchanged fixture hashes',
            'Uninstall silently; verify application and registration removal, data retention')
        limitation = 'CI Windows Server administrator is not another Windows 11 standard-user PC'
    } | ConvertTo-Json -Depth 4
    return
}

$report = [ordered]@{
    schema = 1; status = 'failed'; stage = 'preflight'; failureCode = $null
    expectedVersion = $null; installedVersion = $null; isolatedProfileChecked = $false
    initialInstallExitCode = $null; reinstallExitCode = $null; uninstallExitCode = $null
    executableVerified = $false; registrationVerified = $false
    backgroundStartupResponding = $false; noCodexSessionChecked = $false; applicationExitClean = $null
    observedWindowFound = $false; observedWindowOwned = $false; observedWindowVisible = $false
    observedDispatcherResponding = $false; observedStoreLock = $false; observedProcessExited = $false
    fixtureCreated = $false; reinstallPreservedFixture = $false
    applicationFilesRemoved = $false; registrationRemoved = $false; uninstallPreservedFixture = $false
    durationSeconds = 0
}
$watch = [Diagnostics.Stopwatch]::StartNew()
$failureCode = 'unexpected_error'
$reportFile = $null
$mayWriteReport = $false
$tracker = $null
$runExe = $null
$emptyCodexHome = $null
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C84B03F8-B16D-4A0C-9249-148C613C3581}_is1'

function Stop-Test([string]$Code) {
    $script:failureCode = $Code
    throw [InvalidOperationException]::new($Code)
}

function Assert-NoLink([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            if ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                Stop-Test 'reparse_point_refused'
            }
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
}

function Test-Within([string]$Path, [string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    return $Path.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Start-OwnedProcess([string]$Executable, [string[]]$Arguments,
    [Diagnostics.ProcessWindowStyle]$WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden) {
    Assert-NoLink $Executable
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WindowStyle = $WindowStyle
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN')) {
        $null = $info.Environment.Remove($name)
    }
    $info.Environment['CODEX_HOME'] = $emptyCodexHome
    $process = [Diagnostics.Process]::Start($info)
    if (!$process) { Stop-Test 'process_launch_failed' }
    return $process
}

function Invoke-Installer([string]$Executable, [string[]]$Arguments) {
    $process = Start-OwnedProcess $Executable $Arguments
    try {
        if (!$process.WaitForExit(120000)) {
            # Only the installer process tree started by this disposable-profile test.
            $process.Kill($true); $null = $process.WaitForExit(5000)
            Stop-Test 'installer_timeout'
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}

function Assert-Installed {
    Assert-NoLink $installDirectory
    Assert-NoLink $runExe
    if (!(Test-Path -LiteralPath $runExe -PathType Leaf)) { Stop-Test 'installed_executable_missing' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($runExe).ProductVersion
    if ($version -notmatch ('^' + [Regex]::Escape($ExpectedVersion) + '(?:\+[0-9A-Za-z.-]+)?$')) {
        Stop-Test 'installed_version_mismatch'
    }
    $report.installedVersion = $ExpectedVersion
    $report.executableVerified = $true
    if (!(Test-Path -LiteralPath $registration)) { Stop-Test 'per_user_registration_missing' }
    $installed = Get-ItemProperty -LiteralPath $registration
    if ([IO.Path]::GetFullPath($installed.InstallLocation).TrimEnd('\') -ine $installDirectory -or
        $installed.DisplayVersion -cne $ExpectedVersion -or
        $installed.UninstallString.Trim('"') -ine (Join-Path $installDirectory 'unins000.exe')) {
        Stop-Test 'per_user_registration_mismatch'
    }
    $report.registrationVerified = $true
}

function Close-TestTracker {
    if (!$tracker -or $tracker.HasExited) { return $false }
    $exitProcess = Start-OwnedProcess $runExe @('--exit')
    try {
        if (!$exitProcess.WaitForExit(10000)) {
            $exitProcess.Kill($true); $null = $exitProcess.WaitForExit(3000)
            return $false
        }
        return $exitProcess.ExitCode -eq 0 -and $tracker.WaitForExit(15000) -and $tracker.ExitCode -eq 0
    } finally { $exitProcess.Dispose() }
}

function Write-Fixture([string]$Name, [object]$Value) {
    $path = Join-Path $dataRoot $Name
    Assert-NoLink $path
    # The data directory was absent at preflight and was created by our first startup.
    # Refuse unexpected existing settings rather than replacing even test-created files.
    if (Test-Path -LiteralPath $path) { Stop-Test 'unexpected_fixture_file_exists' }
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

function Test-FixtureHashes {
    foreach ($entry in $fixtureHashes.GetEnumerator()) {
        $path = Join-Path $dataRoot $entry.Key
        Assert-NoLink $path
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.Value) { return $false }
    }
    return $true
}

try {
    # No report directory, work directory or process is created before all these refusals.
    if (!$IsWindows -or ![Environment]::Is64BitOperatingSystem -or !$DedicatedTestProfile) {
        Stop-Test 'dedicated_windows_profile_required'
    }
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or !$SetupPath) {
        Stop-Test 'installer_and_version_required'
    }
    $report.expectedVersion = $ExpectedVersion
    $localData = [Environment]::GetFolderPath('LocalApplicationData')
    $dataRoot = Join-Path $localData 'CodexTracker'
    $installDirectory = Join-Path $localData 'Programs\CodexTracker'
    $defaultCodex = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
    foreach ($path in @($dataRoot, $installDirectory, $defaultCodex)) { Assert-NoLink $path }
    if (Test-Path -LiteralPath $dataRoot) { Stop-Test 'existing_tracker_data_refused' }
    if ((Test-Path -LiteralPath $defaultCodex) -or
        ![string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process'))) {
        Stop-Test 'existing_codex_profile_refused'
    }
    if (@(Get-Process -Name 'CodexTracker*' -ErrorAction SilentlyContinue).Count) { Stop-Test 'existing_tracker_process_refused' }
    foreach ($key in @($registration,
        $registration.Replace('HKCU:', 'HKLM:'),
        $registration.Replace('Software\', 'Software\WOW6432Node\'),
        $registration.Replace('HKCU:', 'HKLM:').Replace('Software\', 'Software\WOW6432Node\'))) {
        if (Test-Path -LiteralPath $key) { Stop-Test 'existing_tracker_installation_refused' }
    }
    foreach ($path in @($installDirectory, (Join-Path $env:ProgramFiles 'CodexTracker'),
        (Join-Path ${env:ProgramFiles(x86)} 'CodexTracker'))) {
        Assert-NoLink $path
        if (Test-Path -LiteralPath $path) { Stop-Test 'existing_tracker_installation_refused' }
    }
    $setup = [IO.Path]::GetFullPath($SetupPath)
    Assert-NoLink $setup
    if (!(Test-Path -LiteralPath $setup -PathType Leaf) -or
        [IO.Path]::GetFileName($setup) -cne "CodexTracker-$ExpectedVersion-Setup.exe") { Stop-Test 'setup_file_invalid' }
    $reportFile = [IO.Path]::GetFullPath($ReportPath)
    Assert-NoLink $reportFile
    if ((Test-Path -LiteralPath $reportFile) -or (Test-Within $reportFile $dataRoot) -or
        (Test-Within $reportFile $installDirectory) -or (Test-Within $reportFile $defaultCodex)) {
        Stop-Test 'report_destination_refused'
    }
    $report.isolatedProfileChecked = $true
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFile))
    $mayWriteReport = $true
    $work = Join-Path ([IO.Path]::GetTempPath()) ('CodexTrackerInstaller-' + [Guid]::NewGuid().ToString('N'))
    $emptyCodexHome = Join-Path $work 'empty-codex-home'
    Assert-NoLink $emptyCodexHome
    $null = [IO.Directory]::CreateDirectory($emptyCodexHome)
    $runExe = Join-Path $installDirectory 'CodexTracker.exe'
    $setupArguments = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=')

    $report.stage = 'initial_install'
    $report.initialInstallExitCode = Invoke-Installer $setup $setupArguments
    if ($report.initialInstallExitCode -ne 0) { Stop-Test 'initial_install_failed' }
    Assert-Installed

    $report.stage = 'first_startup'
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InstallerLifecycleWindow {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    // PowerShell converts $null to an empty string for string parameters. Keep the
    // null class filter inside C# so FindWindow searches every window class.
    public static IntPtr FindTrackerWindow() => FindWindow(null, "Codex Tracker");
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
'@
    # The application's first-start onboarding must be allowed to show its window.
    # Installer processes and the --exit helper retain the default Hidden style.
    $tracker = Start-OwnedProcess $runExe @('--background') -WindowStyle Normal
    $startupWatch = [Diagnostics.Stopwatch]::StartNew()
    while ($startupWatch.Elapsed.TotalSeconds -lt 10 -and !$tracker.HasExited) {
        $handle = [InstallerLifecycleWindow]::FindTrackerWindow()
        $ownerId = 0u; $response = [UIntPtr]::Zero
        # Last-probe booleans only: no window titles, handles, paths or error text in the report.
        $report.observedWindowFound = $handle -ne [IntPtr]::Zero
        $report.observedWindowOwned = $false
        $report.observedWindowVisible = $false
        $report.observedDispatcherResponding = $false
        $report.observedStoreLock = Test-Path -LiteralPath (Join-Path $dataRoot '.lock')
        if ($report.observedWindowFound) {
            $null = [InstallerLifecycleWindow]::GetWindowThreadProcessId($handle, [ref]$ownerId)
            $report.observedWindowOwned = $ownerId -eq $tracker.Id
            if ($report.observedWindowOwned) {
                $report.observedWindowVisible = [InstallerLifecycleWindow]::IsWindowVisible($handle)
                $report.observedDispatcherResponding = [InstallerLifecycleWindow]::SendMessageTimeout(
                    $handle, 0, [UIntPtr]::Zero, [IntPtr]::Zero, 2, 1000, [ref]$response) -ne [IntPtr]::Zero
            }
        }
        # First startup shows onboarding after InitializeAsync even with --background.
        # A hidden HWND or a responding dispatcher alone is not readiness.
        if ($report.observedWindowOwned -and $report.observedWindowVisible -and
            $report.observedDispatcherResponding -and $report.observedStoreLock) {
            $report.backgroundStartupResponding = $true
            break
        }
        Start-Sleep -Milliseconds 100
    }
    $report.observedProcessExited = $tracker.HasExited
    if (!$report.backgroundStartupResponding) { Stop-Test 'background_startup_failed' }
    Start-Sleep -Milliseconds 250
    $report.observedProcessExited = $tracker.HasExited
    $report.observedDispatcherResponding = !$report.observedProcessExited -and
        [InstallerLifecycleWindow]::SendMessageTimeout($handle, 0, [UIntPtr]::Zero, [IntPtr]::Zero, 2, 1000, [ref]$response) -ne [IntPtr]::Zero
    if ($report.observedProcessExited -or !$report.observedDispatcherResponding) {
        $report.backgroundStartupResponding = $false
        Stop-Test 'background_startup_failed'
    }
    $report.noCodexSessionChecked = !(Test-Path -LiteralPath (Join-Path $emptyCodexHome 'auth.json'))
    if (!$report.noCodexSessionChecked) { Stop-Test 'unexpected_codex_session' }
    $report.applicationExitClean = Close-TestTracker
    if (!$report.applicationExitClean) { Stop-Test 'application_exit_failed' }
    if (@(Get-Process -Name 'CodexTracker*' -ErrorAction SilentlyContinue).Count) { Stop-Test 'unexpected_tracker_process' }

    $report.stage = 'fictional_fixture'
    Assert-NoLink $dataRoot
    Write-Fixture 'preferences.json' @{
        themeMode = 'Dark'; refreshMinutes = 5; downloadUpdatesAutomatically = $false; installUpdatesAtStartup = $false
        mcpEnabled = $false; alert20 = $false; alert10 = $false; alert5 = $false
        resetNotifications = $false; expiryNotifications = $false; reminderRules = @()
    }
    Write-Fixture 'settings.json' @{
        accounts = @(
            @{ id = 'b18b439f-29a9-44d3-b6d4-d8e083001001'; email = 'installer.one@example.test' },
            @{ id = 'b18b439f-29a9-44d3-b6d4-d8e083001002'; email = 'installer.two@example.test' })
        selectedAccountId = $null; onboardingComplete = $true; detectedAccountIds = @()
    }
    Write-Fixture 'snapshots.json' @{}
    $fixtureHashes = @{}
    foreach ($name in @('preferences.json', 'settings.json', 'snapshots.json')) {
        $fixtureHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $dataRoot $name) -Algorithm SHA256).Hash
    }
    $report.fixtureCreated = $true

    $report.stage = 'reinstall'
    $report.reinstallExitCode = Invoke-Installer $setup $setupArguments
    if ($report.reinstallExitCode -ne 0) { Stop-Test 'reinstall_failed' }
    Assert-Installed
    $report.reinstallPreservedFixture = Test-FixtureHashes
    if (!$report.reinstallPreservedFixture) { Stop-Test 'reinstall_changed_fixture' }

    $report.stage = 'uninstall'
    # Never execute an arbitrary registry command: require the exact registered test directory.
    Assert-Installed
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    Assert-NoLink $uninstaller
    $report.uninstallExitCode = Invoke-Installer $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ($report.uninstallExitCode -ne 0) { Stop-Test 'uninstall_failed' }
    $removalWatch = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath $installDirectory) -and $removalWatch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    $report.applicationFilesRemoved = !(Test-Path -LiteralPath $installDirectory)
    $report.registrationRemoved = !(Test-Path -LiteralPath $registration)
    $report.uninstallPreservedFixture = Test-FixtureHashes
    if (!$report.applicationFilesRemoved -or !$report.registrationRemoved) { Stop-Test 'uninstall_left_application_state' }
    if (!$report.uninstallPreservedFixture) { Stop-Test 'uninstall_changed_fixture' }
    $report.status = 'passed'; $report.stage = 'complete'
} catch {
    # Deliberately exclude exception text, paths, usernames and fixture contents.
    $report.failureCode = if ($failureCode -eq 'unexpected_error') { 'unexpected_error_in_reported_stage' } else { $failureCode }
} finally {
    if ($tracker) {
        if (!$tracker.HasExited) {
            try { $report.applicationExitClean = Close-TestTracker } catch { $report.applicationExitClean = $false }
            if (!$report.applicationExitClean) {
                # This process handle belongs to the instance created by this test only.
                try { $tracker.Kill($true); $null = $tracker.WaitForExit(5000) } catch { }
                $report.status = 'failed'; $report.failureCode = 'application_exit_failed'
            }
        }
        $tracker.Dispose()
    }
    $report.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
    $json = $report | ConvertTo-Json -Depth 4
    if ($mayWriteReport) {
        try {
            Assert-NoLink $reportFile
            $stream = [IO.File]::Open($reportFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try { $bytes = [Text.Encoding]::UTF8.GetBytes($json); $stream.Write($bytes) } finally { $stream.Dispose() }
        } catch { $report.status = 'failed'; $report.failureCode = 'report_write_failed'; $json = $report | ConvertTo-Json -Depth 4 }
    }
    $json
}
if ($report.status -ne 'passed') { exit 1 }
