#requires -Version 7.0
<#
.SYNOPSIS
Validates an update between public releases in a disposable Windows profile.
.DESCRIPTION
Without -Run, prints the plan only: no files, network requests or processes.
-Run requires -DedicatedTestProfile and refuses existing Tracker or Codex data.
The application downloads its own update; this harness never fabricates a prepared
record or changes the installed user's files. See docs/PUBLISHED-UPDATE-TEST.md.
#>
[CmdletBinding()]
param(
    [switch]$Plan,
    [switch]$Run,
    [switch]$DedicatedTestProfile,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$SourceVersion = '0.9.0',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$TargetVersion = '0.9.1',
    [switch]$IncludePrereleases,
    [ValidateSet('Startup', 'Button')][string]$InstallMode = 'Startup',
    [string]$ReportPath = 'artifacts/published-update/result.json'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Plan -and $Run) { throw 'Choisir -Plan ou -Run, pas les deux.' }
if ([version]$SourceVersion -lt [version]'0.8.4' -or [version]$TargetVersion -le [version]$SourceVersion) {
    throw 'La source doit etre au moins 0.8.4 et la cible strictement plus recente.'
}
$channelAware = [version]$SourceVersion -ge [version]'0.9.0'
$channel = if ($IncludePrereleases) { 'preview' } else { 'stable' }
if (!$Run) {
    [ordered]@{
        mode = 'plan'; writesFiles = $false; usesNetwork = $false; launchesApplication = $false
        sourceVersion = $SourceVersion; targetVersion = $TargetVersion; installMode = $InstallMode
        includePrereleases = [bool]$IncludePrereleases; channelAwareSource = $channelAware
        requires = @('Windows x64; PowerShell 7', 'Profil Windows jetable ou runner CI vierge',
            'Aucune donnee Codex/Tracker, aucune installation ou instance Tracker',
            'Opt-in explicite -Run -DedicatedTestProfile')
        steps = @('Verifier une fois les Releases publiques, arreter si GitHub limite les requetes',
            'Verifier le ZIP source et son EXE, creer exclusivement des donnees fictives',
            'Laisser le tracker normal telecharger la cible (maximum 5 minutes)',
            'Verifier le paquet public et le bouton via UI Automation',
            'Installer par le mode choisi et verifier le resultat et les donnees',
            'Quitter uniquement cette instance, conserver les fichiers du test')
        humanButtonObservation = 'non confirmee; UI Automation est une preuve distincte'
    } | ConvertTo-Json -Depth 5
    return
}

$report = [ordered]@{
    schema = 2; startedAt = [DateTimeOffset]::UtcNow.ToString('o'); finishedAt = $null
    sourceVersion = $SourceVersion; targetVersion = $TargetVersion; installMode = $InstallMode
    includePrereleases = [bool]$IncludePrereleases; channelAwareSource = $channelAware
    status = 'failed'; stage = 'preflight'; failureCode = $null; retryAt = $null
    isolatedProfileChecked = $false; publicMetadataVerified = $false
    sourceArchiveVerified = $false; sourceExecutableVerified = $false
    automaticDownloadVerified = $false; targetArchiveVerified = $false
    targetExecutableVerified = $false; buttonVisible = $null; buttonEnabled = $null
    buttonInvoked = $false; humanButtonObserved = $null
    startupInstallRequested = $false; installSucceeded = $false
    targetProcessResponding = $false; fixtureDataPreserved = $false
    sourceExecutableSha256 = $null; targetExecutableSha256 = $null
    applicationExitClean = $null; durationSeconds = 0
}
$watch = [Diagnostics.Stopwatch]::StartNew()
$http = $null
$runExe = $null
$ownedRun = $false
$mayWriteReport = $false
$fixturePaths = @()
$previousCodexHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
$failureCode = 'unexpected_error'

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

function Write-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
}

function Read-Json([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 1MB) { Stop-Test 'unexpected_json_size' }
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json
}

function Get-PublicResource([Uri]$Uri, [long]$Maximum, [string]$Destination = '') {
    # Only HTTPS GitHub URLs, no token, cookies, credentials or arbitrary redirect.
    $allowed = @('api.github.com', 'github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
    try {
        for ($hop = 0; $hop -le 3; $hop++) {
            if ($Uri.Scheme -ne 'https' -or !$Uri.IsDefaultPort -or $Uri.UserInfo -or $Uri.Host -notin $allowed) {
                Stop-Test 'unexpected_public_url'
            }
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
            $request.Headers.UserAgent.ParseAdd('CodexTrackerPublishedUpdateValidation/1.0')
            if ($Uri.Host -eq 'api.github.com') {
                $request.Headers.Accept.ParseAdd('application/vnd.github+json')
                $request.Headers.Add('X-GitHub-Api-Version', '2022-11-28')
            }
            $response = $null
            try {
                $response = $http.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
                $status = [int]$response.StatusCode
                if ($status -in @(403, 429)) {
                    $report.status = 'blocked'
                    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(1)
                    if ($response.Headers.Contains('X-RateLimit-Reset')) {
                        $seconds = 0L
                        if ([long]::TryParse(($response.Headers.GetValues('X-RateLimit-Reset') | Select-Object -First 1), [ref]$seconds)) {
                            try { $reset = [DateTimeOffset]::FromUnixTimeSeconds($seconds); if ($reset -gt $deadline) { $deadline = $reset } } catch { }
                        }
                    }
                    $retryAfter = $response.Headers.RetryAfter
                    if ($retryAfter -and $retryAfter.Date -and $retryAfter.Date -gt $deadline) { $deadline = $retryAfter.Date }
                    if ($retryAfter -and $retryAfter.Delta) {
                        $reset = [DateTimeOffset]::UtcNow.Add($retryAfter.Delta)
                        if ($reset -gt $deadline) { $deadline = $reset }
                    }
                    $report.retryAt = $deadline.ToString('o')
                    Stop-Test 'github_rate_limited_no_retry'
                }
                if ($status -in @(301, 302, 303, 307, 308)) {
                    $next = $response.Headers.Location
                    if ($Uri.Host -eq 'api.github.com' -or !$next -or !$next.IsAbsoluteUri -or
                        $next.Host -notin @('release-assets.githubusercontent.com', 'objects.githubusercontent.com')) {
                        Stop-Test 'unexpected_public_redirect'
                    }
                    $Uri = $next
                    continue
                }
                if (!$response.IsSuccessStatusCode) { Stop-Test 'github_http_failure_no_retry' }
                if ($response.Headers.Contains('Link') -and (($response.Headers.GetValues('Link') -join ',') -match 'rel="next"')) {
                    Stop-Test 'release_list_requires_pagination'
                }
                if ($response.Content.Headers.ContentLength -gt $Maximum) { Stop-Test 'public_resource_too_large' }
                $inputStream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
                $outputStream = if ($Destination) {
                    [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                } else { [IO.MemoryStream]::new() }
                try {
                    $buffer = [byte[]]::new(65536)
                    $total = 0L
                    while (($count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                        $total += $count
                        if ($total -gt $Maximum) { Stop-Test 'public_resource_too_large' }
                        $outputStream.Write($buffer, 0, $count)
                    }
                    if (!$Destination) { return ,$outputStream.ToArray() }
                    return
                } finally { $inputStream.Dispose(); $outputStream.Dispose() }
            } finally { if ($response) { $response.Dispose() }; $request.Dispose() }
        }
        Stop-Test 'too_many_public_redirects'
    } finally { $timeout.Dispose() }
}

function Get-PublicAsset([object]$Release, [string]$Version, [string]$Suffix) {
    $name = "CodexTracker-$Version-win-x64.zip$Suffix"
    $assets = @($Release.assets | Where-Object name -eq $name)
    if ($assets.Count -ne 1 -or $assets[0].size -le 0 -or $assets[0].size -gt 512MB -or
        $assets[0].browser_download_url -cne "https://github.com/Aleqsd/codex-tracker/releases/download/v$Version/$name") {
        Stop-Test 'public_release_asset_invalid'
    }
    return $assets[0]
}

function Get-PublicChecksum([object]$Release, [string]$Version) {
    $asset = Get-PublicAsset $Release $Version '.sha256'
    $value = [Text.Encoding]::ASCII.GetString((Get-PublicResource $asset.browser_download_url 4096))
    $name = [Regex]::Escape("CodexTracker-$Version-win-x64.zip")
    if ($value.Trim() -notmatch "\A([0-9a-fA-F]{64})[ \t]+\*?$name\z") { Stop-Test 'public_checksum_invalid' }
    return $Matches[1].ToLowerInvariant()
}

function Read-ZipExecutable([string]$Archive, [string]$Destination = '') {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $entries = @($zip.Entries | Where-Object FullName -CEQ 'CodexTracker.exe')
        if ($zip.Entries.Count -gt 1024 -or $entries.Count -ne 1 -or $entries[0].Length -lt 2 -or $entries[0].Length -gt 512MB) {
            Stop-Test 'public_archive_invalid'
        }
        $stream = $entries[0].Open()
        try {
            if ($Destination) {
                $file = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew)
                try { $stream.CopyTo($file) } finally { $file.Dispose() }
                if ((Get-Item -LiteralPath $Destination).Length -ne $entries[0].Length) { Stop-Test 'executable_size_mismatch' }
                return (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            $sha = [Security.Cryptography.SHA256]::Create()
            try { return [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose() }
        } finally { $stream.Dispose() }
    } finally { $zip.Dispose() }
}

function Start-TestTracker([string[]]$Arguments = @()) {
    $info = [Diagnostics.ProcessStartInfo]::new($runExe)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($runExe)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    # Neither the application nor its children receive a CI GitHub token.
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN')) { $null = $info.Environment.Remove($name) }
    $info.Environment['CODEX_HOME'] = $emptyCodexHome
    $process = [Diagnostics.Process]::Start($info)
    if (!$process) { Stop-Test 'application_launch_failed' }
    return $process
}

function Get-TestProcesses {
    return @(Get-Process -Name CodexTracker -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq $runExe } catch { $false }
    })
}

function Close-TestTracker {
    $processes = @(Get-TestProcesses)
    if (!$processes.Count) { return $true }
    $exitProcess = Start-TestTracker @('--exit')
    try {
        if (!$exitProcess.WaitForExit(10000)) { return $false }
        foreach ($process in $processes) { if (!$process.WaitForExit(15000)) { return $false } }
        return $true
    } finally { $exitProcess.Dispose() }
}

function Invoke-ButtonProbe([int]$TrackerId, [string]$Mode) {
    $helper = Join-Path $PSScriptRoot 'published-update-ui.ps1'
    if (!(Test-Path -LiteralPath $helper)) { return $null }
    $info = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $helper, '-ProcessId', "$TrackerId", '-Mode', $Mode)) { $info.ArgumentList.Add($argument) }
    $probe = [Diagnostics.Process]::Start($info)
    try {
        $output = $probe.StandardOutput.ReadToEndAsync()
        $errors = $probe.StandardError.ReadToEndAsync()
        if (!$probe.WaitForExit(20000)) {
            # This is only our disposable UIA helper, never Tracker or Codex.
            $probe.Kill(); $null = $probe.WaitForExit(3000)
            return $null
        }
        if ($probe.ExitCode -ne 0) { return $null }
        try { return $output.GetAwaiter().GetResult() | ConvertFrom-Json } catch { return $null }
    } finally { $probe.Dispose() }
}

try {
    if (!$IsWindows -or ![Environment]::Is64BitOperatingSystem -or !$DedicatedTestProfile) { Stop-Test 'dedicated_windows_profile_required' }
    # A refusal can still leave the explicit, sanitized report requested by CI.
    # This destination is unrelated to application data and is never overwritten.
    $reportFile = [IO.Path]::GetFullPath($ReportPath)
    Assert-NoLink $reportFile
    if (Test-Path -LiteralPath $reportFile) { Stop-Test 'existing_report_refused' }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportFile))
    $mayWriteReport = $true
    $dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodexTracker'
    $defaultCodex = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex'
    Assert-NoLink $dataRoot
    if (Test-Path -LiteralPath $dataRoot) { Stop-Test 'existing_tracker_data_refused' }
    if ((Test-Path -LiteralPath $defaultCodex) -or ![string]::IsNullOrWhiteSpace($previousCodexHome)) { Stop-Test 'existing_codex_profile_refused' }
    if (@(Get-Process -Name 'CodexTracker*' -ErrorAction SilentlyContinue).Count) { Stop-Test 'existing_tracker_process_refused' }
    $registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C84B03F8-B16D-4A0C-9249-148C613C3581}_is1'
    if (Test-Path -LiteralPath $registration) { Stop-Test 'existing_tracker_installation_refused' }
    $report.isolatedProfileChecked = $true
    $work = Join-Path ([IO.Path]::GetTempPath()) ('CodexTrackerPublishedUpdate-' + [Guid]::NewGuid().ToString('N'))
    Assert-NoLink $work
    $null = [IO.Directory]::CreateDirectory($work)
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false; $handler.UseCookies = $false; $handler.UseDefaultCredentials = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [TimeSpan]::FromMinutes(5)

    $report.stage = 'public_metadata'
    $releases = [Text.Encoding]::UTF8.GetString((Get-PublicResource 'https://api.github.com/repos/Aleqsd/codex-tracker/releases?per_page=100&page=1' 4MB)) | ConvertFrom-Json
    $source = @($releases | Where-Object { !$_.draft -and $_.tag_name -ceq "v$SourceVersion" })
    $target = @($releases | Where-Object { !$_.draft -and $_.tag_name -ceq "v$TargetVersion" })
    if ($source.Count -ne 1 -or $target.Count -ne 1) { Stop-Test 'required_public_release_missing' }
    if ($target[0].prerelease -and !$IncludePrereleases) { Stop-Test 'prerelease_requires_explicit_opt_in' }
    # A legacy source selects all channels; modern sources respect the fixture's
    # explicit opt-in. Refuse a newer eligible release instead of another target.
    foreach ($release in $releases) {
        if ($channelAware -and !$IncludePrereleases -and ($release.prerelease -or $release.tag_name -match '-')) { continue }
        if (!$release.draft -and $release.tag_name -match '^v?(\d+\.\d+\.\d+)(?:[-+].*)?$' -and
            [version]$Matches[1] -gt [version]$TargetVersion) { Stop-Test 'newer_release_requires_recipe_review' }
    }
    $sourceAsset = Get-PublicAsset $source[0] $SourceVersion ''
    $targetAsset = Get-PublicAsset $target[0] $TargetVersion ''
    $sourceChecksum = Get-PublicChecksum $source[0] $SourceVersion
    $targetChecksum = Get-PublicChecksum $target[0] $TargetVersion
    $report.publicMetadataVerified = $true

    $report.stage = 'source_download'
    $sourceArchive = Join-Path $work 'source.zip'
    Get-PublicResource $sourceAsset.browser_download_url $sourceAsset.size $sourceArchive
    if ((Get-Item -LiteralPath $sourceArchive).Length -ne $sourceAsset.size -or
        (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceChecksum) { Stop-Test 'source_archive_hash_mismatch' }
    $report.sourceArchiveVerified = $true
    $installDirectory = Join-Path $work 'application'
    $null = [IO.Directory]::CreateDirectory($installDirectory)
    $runExe = Join-Path $installDirectory 'CodexTracker.exe'
    $report.sourceExecutableSha256 = Read-ZipExecutable $sourceArchive $runExe
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($runExe).ProductVersion -notmatch ('^' + [Regex]::Escape($SourceVersion) + '(?:\+|$)')) { Stop-Test 'source_version_mismatch' }
    $report.sourceExecutableVerified = $true

    $report.stage = 'fictional_fixture'
    # Check again immediately before creating data, after the public network work.
    if ((Test-Path -LiteralPath $dataRoot) -or (Test-Path -LiteralPath $defaultCodex) -or
        @(Get-Process -Name 'CodexTracker*' -ErrorAction SilentlyContinue).Count) { Stop-Test 'profile_changed_during_preflight' }
    $null = [IO.Directory]::CreateDirectory($dataRoot)
    $emptyCodexHome = Join-Path $work 'empty-codex-home'
    $null = [IO.Directory]::CreateDirectory($emptyCodexHome)
    $first = 'b18b439f-29a9-44d3-b6d4-d8e083001001'
    $second = 'b18b439f-29a9-44d3-b6d4-d8e083001002'
    Write-Json (Join-Path $dataRoot 'settings.json') ([ordered]@{
        accounts = @(@{ id = $first; email = 'demo.one@example.org' }, @{ id = $second; email = 'demo.two@example.org' })
        selectedAccountId = $null; onboardingComplete = $true; detectedAccountIds = @($first)
    })
    Write-Json (Join-Path $dataRoot 'preferences.json') ([ordered]@{
        themeMode = 'Dark'; refreshMinutes = 5; downloadUpdatesAutomatically = $true; installUpdatesAtStartup = $true
        includePrereleaseUpdates = [bool]$IncludePrereleases
        mcpEnabled = $false; alert20 = $false; alert10 = $false; alert5 = $false
        resetNotifications = $false; expiryNotifications = $false; reminderRules = @()
    })
    $observed = [DateTimeOffset]::UtcNow.ToString('o')
    $expires = [DateTimeOffset]::UtcNow.AddDays(5).ToString('o')
    Write-Json (Join-Path $dataRoot 'snapshots.json') @{
        $first = @{ email = 'demo.one@example.org'; planType = 'plus'; fetchedAt = $observed
            buckets = @(@{ id = 'codex'; name = 'Codex'; windows = @(@{ usedPercent = 42; windowDurationMins = 10080; resetsAt = $expires }) })
            availableResetCredits = 0; resetCredits = @() }
    }
    $usageDirectory = Join-Path $dataRoot 'usage'
    $null = [IO.Directory]::CreateDirectory($usageDirectory)
    $usagePath = Join-Path $usageDirectory "$first.json"
    Write-Json $usagePath @{ accountId = $first; alerts = @{}; samples = @(@{ accountId = $first; timestamp = $observed; weeklyRemaining = 58; weeklyResetsAt = $expires }) }
    $fixturePaths = @((Join-Path $dataRoot 'settings.json'), (Join-Path $dataRoot 'preferences.json'), (Join-Path $dataRoot 'snapshots.json'), $usagePath)
    $fixtureHashes = @($fixturePaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })

    $report.stage = 'automatic_download'
    $ownedRun = $true
    $sourceProcess = Start-TestTracker
    $updatesDirectory = Join-Path $dataRoot 'updates'
    $preparedName = if ($channelAware) { "prepared-update-$channel.json" } else { 'prepared-update.json' }
    $cacheName = if ($channelAware -and $IncludePrereleases) { 'release-cache-preview.json' } else { 'release-cache.json' }
    $preparedPath = Join-Path $updatesDirectory $preparedName
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
    while (!(Test-Path -LiteralPath $preparedPath)) {
        $sourceProcess.Refresh()
        if ($sourceProcess.HasExited) { Stop-Test 'source_application_exited' }
        $cachePath = Join-Path $updatesDirectory $cacheName
        if (Test-Path -LiteralPath $cachePath) {
            $cache = Read-Json $cachePath
            if ($cache.RateLimited) {
                $report.status = 'blocked'; $report.retryAt = ([DateTimeOffset]$cache.NextCheckAt).ToString('o')
                Stop-Test 'application_github_rate_limited_no_retry'
            }
            if ($cache.Failures -gt 0) { Stop-Test 'application_public_check_failed_no_retry' }
        }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { Stop-Test 'automatic_download_deadline' }
        Start-Sleep -Milliseconds 500
    }
    $prepared = Read-Json $preparedPath
    $stageId = [Guid]::Empty
    if ($prepared.Release.Version -ne $TargetVersion -or ![Guid]::TryParseExact($prepared.StageId, 'N', [ref]$stageId) -or $prepared.AutomaticAttempted) { Stop-Test 'unexpected_prepared_update' }
    $stagePath = Join-Path $updatesDirectory $stageId.ToString('N')
    Assert-NoLink $stagePath
    $downloadedArchive = Join-Path $stagePath 'package.zip'
    $payload = Join-Path $stagePath 'payload/CodexTracker.exe'
    if ((Get-Item -LiteralPath $downloadedArchive).Length -ne $targetAsset.size -or
        (Get-FileHash -LiteralPath $downloadedArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $targetChecksum) { Stop-Test 'target_archive_hash_mismatch' }
    $report.targetArchiveVerified = $true
    $expectedTargetHash = Read-ZipExecutable $downloadedArchive
    if ((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedTargetHash -or $prepared.ExecutableHash -ne $expectedTargetHash -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo($payload).ProductVersion -notmatch ('^' + [Regex]::Escape($TargetVersion) + '(?:\+|$)')) { Stop-Test 'target_payload_mismatch' }
    $report.targetExecutableSha256 = $expectedTargetHash
    $report.targetExecutableVerified = $true
    $report.automaticDownloadVerified = $true

    $report.stage = 'button_observation'
    # Preparation is persisted just before WPF receives its UI event.
    Start-Sleep -Milliseconds 750
    $button = Invoke-ButtonProbe $sourceProcess.Id 'Observe'
    if ($button) { $report.buttonVisible = [bool]$button.buttonVisible; $report.buttonEnabled = [bool]$button.buttonEnabled }
    $report.stage = 'install'
    if ($InstallMode -eq 'Button') {
        if (!$report.buttonVisible -or !$report.buttonEnabled) { Stop-Test 'button_not_available_for_invocation' }
        $invocation = Invoke-ButtonProbe $sourceProcess.Id 'Invoke'
        if (!$invocation -or !$invocation.invoked) { Stop-Test 'button_invocation_failed' }
        $report.buttonInvoked = $true
    } else {
        if (!(Close-TestTracker)) { Stop-Test 'source_application_did_not_close' }
        $startupProcess = Start-TestTracker
        $report.startupInstallRequested = $true
    }
    $resultPath = Join-Path $updatesDirectory 'last-result.json'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
    while (!(Test-Path -LiteralPath $resultPath)) {
        if ([DateTimeOffset]::UtcNow -gt $deadline) { Stop-Test 'installation_result_deadline' }
        Start-Sleep -Milliseconds 500
    }
    # The published helper writes this small result without an atomic rename.
    # File creation can precede JSON completion: retry only the local read for 2s.
    $result = $null
    $readDeadline = [DateTimeOffset]::UtcNow.AddSeconds(2)
    do {
        try { $result = Read-Json $resultPath } catch { }
        if ($result) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $readDeadline)
    if (!$result) { Stop-Test 'installation_result_unreadable' }
    if (!$result.Success) { Stop-Test 'application_update_reported_failure' }
    if ((Get-FileHash -LiteralPath $runExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedTargetHash) { Stop-Test 'installed_executable_hash_mismatch' }
    $report.installSucceeded = $true
    $targetProcesses = @(Get-TestProcesses)
    if ($targetProcesses.Count -ne 1 -or !$targetProcesses[0].Responding) { Stop-Test 'target_application_not_responding' }
    $report.targetProcessResponding = $true
    $report.stage = 'data_preservation'
    $currentHashes = @($fixturePaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
    if (($fixtureHashes -join ':') -ne ($currentHashes -join ':')) { Stop-Test 'fixture_data_changed' }
    $report.fixtureDataPreserved = $true
    $report.status = if ($report.buttonVisible -and $report.buttonEnabled) { 'passed' } else { 'partial' }
    $report.stage = 'complete'
} catch {
    # No exception text, URLs with query strings, paths, usernames, account rows or
    # GitHub headers are copied to the shareable report.
    $report.failureCode = if ($failureCode -ne 'unexpected_error') { $failureCode } else { 'unexpected_error_in_reported_stage' }
} finally {
    if ($ownedRun -and $runExe) {
        try { $report.applicationExitClean = Close-TestTracker } catch { $report.applicationExitClean = $false }
        if (!$report.applicationExitClean) { $report.status = 'failed'; $report.failureCode = 'test_application_did_not_close' }
    }
    if ($http) { $http.Dispose() }
    $report.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
    $report.finishedAt = [DateTimeOffset]::UtcNow.ToString('o')
    if ($mayWriteReport) { Write-Json $reportFile $report }
    $report | ConvertTo-Json -Depth 5
}
if ($report.status -ne 'passed') { exit 1 }
