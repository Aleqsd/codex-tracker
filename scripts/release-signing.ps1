# Shared checks for the optional signing workflow. No credentials or signing calls here.
function Get-ReleaseIdentity([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Release binary missing.' }
    [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
}

function Assert-ReleaseIdentity([string]$Path, [string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
    $identity = Get-ReleaseIdentity $Path
    if ($identity.ProductName.Trim() -cne 'Codex Tracker' -or
        $identity.ProductVersion.Trim() -cnotmatch ('^' + [regex]::Escape($Version) + '(?:\+[0-9A-Za-z.-]+)?$')) {
        throw 'Release binary product or version mismatch.'
    }
    return $identity
}

function Assert-ReleaseSignature([string]$Path, [string]$ExpectedSignerSubject) {
    if ([string]::IsNullOrWhiteSpace($ExpectedSignerSubject)) { throw 'Expected signing certificate subject is required.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
        throw 'A valid embedded Authenticode signature is required.'
    }
    if (!$signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne $ExpectedSignerSubject) { throw 'Unexpected signing certificate subject.' }
    if (!$signature.TimeStamperCertificate) { throw 'A timestamped signature is required.' }
    if (@($signature.SignerCertificate.EnhancedKeyUsageList | Where-Object {
        [string]$_.ObjectId -eq '1.3.6.1.5.5.7.3.3'
    }).Count -eq 0) { throw 'The certificate is not valid for code signing.' }
    return [ordered]@{
        status = 'Valid'; subject = $signature.SignerCertificate.Subject
        thumbprint = $signature.SignerCertificate.Thumbprint; timestamped = $true
    }
}

function New-ReleaseBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PublishDirectory,
        [Parameter(Mandatory)][string]$InstallerPath,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$Version,
        [switch]$UnsignedRehearsal,
        [string]$ExpectedSignerSubject
    )
    $publish = [IO.Path]::GetFullPath($PublishDirectory)
    $installer = [IO.Path]::GetFullPath($InstallerPath)
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; existing bundles are never overwritten.' }
    if ($output.StartsWith($publish.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The output must be outside the publish directory.'
    }
    $exe = Join-Path $publish 'CodexTracker.exe'
    $null = Assert-ReleaseIdentity $exe $Version
    $null = Assert-ReleaseIdentity $installer $Version
    if ([IO.Path]::GetFileName($installer) -cne "CodexTracker-$Version-Setup.exe") { throw 'Unexpected installer filename.' }
    $appSignature = $null; $setupSignature = $null
    # Fail before creating deliverables if either binary is unsigned or untrusted.
    if (!$UnsignedRehearsal) {
        $appSignature = Assert-ReleaseSignature $exe $ExpectedSignerSubject
        $setupSignature = Assert-ReleaseSignature $installer $ExpectedSignerSubject
    }
    $null = [IO.Directory]::CreateDirectory($output)
    $zip = Join-Path $output "CodexTracker-$Version-win-x64.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
    $setup = Join-Path $output ([IO.Path]::GetFileName($installer))
    Copy-Item -LiteralPath $installer -Destination $setup
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Value "$zipHash  $([IO.Path]::GetFileName($zip))" -Encoding ascii
    [ordered]@{
        schema = 1; version = $Version; signed = !$UnsignedRehearsal
        mode = $(if ($UnsignedRehearsal) { 'unsigned-rehearsal' } else { 'signed-candidate' })
        applicationSignature = $appSignature; installerSignature = $setupSignature
        applicationSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        installerSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        zipSha256 = $zipHash
    }
}
