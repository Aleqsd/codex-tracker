param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][a-zA-Z0-9.-]+)?$')]
    [string]$Version = '0.8.3',
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$Iscc,
    [switch]$InstallCompiler,
    [switch]$TestBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot "artifacts/publish/$Version" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'CodexTracker.exe'))) { throw "Publiez d'abord CodexTracker.exe avec scripts/publish.ps1." }
if (-not $Iscc) {
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compilerCommand) { $Iscc = $compilerCommand.Source }
    $compilerCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'CodexTrackerBuildTools\InnoSetup\ISCC.exe')
    )
    if (-not $Iscc) { $Iscc = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1 }
}
if (-not $Iscc -and $InstallCompiler) {
    # Official immutable release linked at https://jrsoftware.org/isdl.php.
    $toolsDirectory = Join-Path $env:LOCALAPPDATA 'CodexTrackerBuildTools'
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    $compilerInstaller = Join-Path $toolsDirectory 'innosetup-6.7.3.exe'
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $compilerInstaller
    $signature = Get-AuthenticodeSignature -LiteralPath $compilerInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') {
        throw 'La signature officielle du compilateur Inno Setup est invalide.'
    }
    $compilerDirectory = Join-Path $toolsDirectory 'InnoSetup'
    $compilerArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$compilerDirectory`"", '/NOICONS')
    $installation = Start-Process -FilePath $compilerInstaller -ArgumentList $compilerArguments -WindowStyle Hidden -PassThru -Wait
    if ($installation.ExitCode -ne 0) { throw "Installation du compilateur impossible ($($installation.ExitCode))." }
    $Iscc = Join-Path $compilerDirectory 'ISCC.exe'
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) {
    throw 'Inno Setup est introuvable. Installez-le depuis https://jrsoftware.org/isdl.php ou utilisez -InstallCompiler.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compilerParameters = @('/Qp', "/DAppVersion=$Version", "/DPublishDir=$PublishDirectory", "/DArtifactDir=$OutputDirectory")
if ($TestBuild) { $compilerParameters += '/DTestBuild=1' }
$compilerParameters += (Join-Path $repoRoot 'installer\CodexTracker.iss')
& $Iscc @compilerParameters
if ($LASTEXITCODE -ne 0) { throw "La compilation de l'installateur a échoué." }
$suffix = if ($TestBuild) { 'TestSetup' } else { 'Setup' }
$installerPath = Join-Path $OutputDirectory "CodexTracker-$Version-$suffix.exe"
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$installerPath.sha256" -Value "$hash  $(Split-Path -Leaf $installerPath)" -Encoding ascii
Write-Output $installerPath
