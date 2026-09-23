#requires -Version 7.0
param([string]$SignedProbePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/release-signing.ps1"
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock]$Action, [string]$ExpectedError) {
    try { & $Action; throw 'Expected rejection was absent.' }
    catch { Check ($_.Exception.Message -like "*$ExpectedError*") "Wrong rejection: $($_.Exception.Message)" }
}

# Real Windows API: an unsigned script cannot pass the production verifier.
Reject { Assert-ReleaseSignature $PSCommandPath 'CN=Fictional Publisher' } 'valid embedded Authenticode'
$realSignedProbe = $false
if (!$SignedProbePath) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $SignedProbePath = $dotnetCommand.Source }
}
if ($SignedProbePath) {
    $probe = Get-AuthenticodeSignature -LiteralPath $SignedProbePath
    $null = Assert-ReleaseSignature $SignedProbePath $probe.SignerCertificate.Subject
    Check $true 'Real signed SDK executable should be accepted.'
    $realSignedProbe = $true
}

# Controlled results exercise acceptance/fail-closed logic without a certificate,
# changing trust stores, contacting SignPath or claiming real signature validation.
$script:fakeProduct = 'Codex Tracker'
$script:fakeVersion = '1.2.3+abc123'
$script:fakeStatus = 'Valid'
$script:fakeSubject = 'CN=Fictional Publisher'
$script:fakeTimestamp = $true
$script:fakeEku = '1.3.6.1.5.5.7.3.3'
$script:fakeType = 'Authenticode'
$script:rejectInstaller = $false
function Get-ReleaseIdentity([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { throw 'Release binary missing.' }
    [pscustomobject]@{ ProductName=$script:fakeProduct; ProductVersion=$script:fakeVersion }
}
function Get-AuthenticodeSignature([string]$LiteralPath) {
    [pscustomobject]@{
        Status=$(if ($script:rejectInstaller -and $LiteralPath.EndsWith('Setup.exe')) { 'NotSigned' } else { $script:fakeStatus })
        SignatureType=$script:fakeType
        SignerCertificate=[pscustomobject]@{
            Subject=$script:fakeSubject; Thumbprint='FICTIONAL'
            EnhancedKeyUsageList=@([pscustomobject]@{ ObjectId=$script:fakeEku })
        }
        TimeStamperCertificate=$(if ($script:fakeTimestamp) { [pscustomobject]@{Subject='CN=Fictional Timestamp'} } else { $null })
    }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('CodexTrackerSigningTests-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $root 'publish'
$null = [IO.Directory]::CreateDirectory($publish)
[IO.File]::WriteAllText((Join-Path $publish 'CodexTracker.exe'), 'fictional executable')
[IO.File]::WriteAllText((Join-Path $publish 'README.md'), 'fictional documentation')
$installer = Join-Path $root 'CodexTracker-1.2.3-Setup.exe'
[IO.File]::WriteAllText($installer, 'fictional installer')
$exe = Join-Path $publish 'CodexTracker.exe'
$expected = 'CN=Fictional Publisher'
$null = Assert-ReleaseIdentity $exe '1.2.3'
$null = Assert-ReleaseSignature $exe $expected
Check $true 'Mock acceptance failed.'
Reject { Assert-ReleaseIdentity $exe '1.2.4' } 'version mismatch'
Reject { Assert-ReleaseIdentity $exe '../invalid' } 'Invalid release version'
$script:fakeProduct = 'Different App'
Reject { Assert-ReleaseIdentity $exe '1.2.3' } 'product or version mismatch'
$script:fakeProduct = 'Codex Tracker   '; $script:fakeVersion = '1.2.3   '
$null = Assert-ReleaseIdentity $exe '1.2.3'
Check $true 'Inno resource padding must be supported.'
foreach ($status in @('NotSigned', 'HashMismatch', 'NotTrusted', 'UnknownError')) {
    $script:fakeStatus = $status
    Reject { Assert-ReleaseSignature $exe $expected } 'valid embedded Authenticode'
}
$script:fakeStatus = 'Valid'; $script:fakeType = 'Catalog'
Reject { Assert-ReleaseSignature $exe $expected } 'valid embedded Authenticode'
$script:fakeType = 'Authenticode'
Reject { Assert-ReleaseSignature $exe '' } 'subject is required'
Reject { Assert-ReleaseSignature $exe 'CN=Another Publisher' } 'Unexpected signing certificate'
$script:fakeTimestamp = $false
Reject { Assert-ReleaseSignature $exe $expected } 'timestamped signature'
$script:fakeTimestamp = $true; $script:fakeEku = '1.3.6.1.5.5.7.3.1'
Reject { Assert-ReleaseSignature $exe $expected } 'code signing'
$script:fakeEku = '1.3.6.1.5.5.7.3.3'
$argsBundle = @{ PublishDirectory=$publish; InstallerPath=$installer; Version='1.2.3'; ExpectedSignerSubject=$expected }
$script:rejectInstaller = $true
$rejectedOutput = Join-Path $root 'rejected'
Reject { New-ReleaseBundle @argsBundle -OutputDirectory $rejectedOutput } 'valid embedded Authenticode'
Check (!(Test-Path -LiteralPath $rejectedOutput)) 'An unsigned installer must produce no release bundle.'
$script:rejectInstaller = $false
Reject { New-ReleaseBundle @argsBundle -OutputDirectory (Join-Path $publish 'nested') } 'outside the publish'
$output = Join-Path $root 'signed-mock'
$report = New-ReleaseBundle @argsBundle -OutputDirectory $output
Check ($report.signed -and $report.mode -eq 'signed-candidate') 'Wrong verified mode.'
Check (@(Get-ChildItem -LiteralPath $output -File).Count -eq 3) 'Expected exactly Setup, ZIP and ZIP checksum.'
$zip = Join-Path $output 'CodexTracker-1.2.3-win-x64.zip'
Check ((Get-Content -LiteralPath "$zip.sha256").StartsWith((Get-FileHash $zip).Hash.ToLowerInvariant())) 'Checksum must describe final ZIP.'
Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $root 'expanded')
Check ((Get-FileHash (Join-Path $root 'expanded/CodexTracker.exe')).Hash -eq (Get-FileHash $exe).Hash) 'ZIP must contain the verified executable.'
Check ((Get-Content (Join-Path $root 'expanded/README.md')) -eq 'fictional documentation') 'Documentation was lost.'
Reject { New-ReleaseBundle @argsBundle -OutputDirectory $output } 'existing bundles are never overwritten'
$script:fakeStatus = 'NotSigned'
$report = New-ReleaseBundle @argsBundle -OutputDirectory (Join-Path $root 'rehearsal') -UnsignedRehearsal
Check (!$report.signed -and $report.mode -eq 'unsigned-rehearsal' -and !$report.applicationSignature) 'Rehearsal must never claim a signature.'
foreach ($name in @('application.xml', 'installer.xml')) {
    [xml]$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot "../installer/signpath/$name") -Raw
    Check ($config.DocumentElement.NamespaceURI -eq 'http://signpath.io/artifact-configuration/v1') 'Invalid artifact namespace.'
}
[pscustomobject]@{ checks=$checks; status='passed'; signpathAcceptance='not-tested'; realSignedSdkVerification=$realSignedProbe; realUnsignedRejection=$true } | ConvertTo-Json
# All files remain in our unique temp directory; no user directory or certificate is altered.
