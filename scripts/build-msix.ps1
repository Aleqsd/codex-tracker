#requires -Version 7.0
<#
.SYNOPSIS
Packages the published build as an MSIX for the Microsoft Store (artifacts/CodexTracker-<version>.msix).
.DESCRIPTION
The Store signs the package itself: no certificate is needed. -IdentityName and -Publisher must be the values that
Partner Center shows under Product identity (Package/Identity/Name and Package/Identity/Publisher). Without parameters
they come from %USERPROFILE%\.codex-tracker\store-identity.json ({"IdentityName", "Publisher", "PublisherDisplayName"},
outside the repo); otherwise local placeholders only let the package be built and inspected. The package is full trust, with file and registry write virtualization
disabled so that it keeps the classic installation's data in %LOCALAPPDATA%\CodexTracker, and declares the
`codex-tracker.exe` execution alias used for startup, MCP and Claude Code (AppPackage.LaunchPath). It also carries the
Windows 11 widget (src/CodexTracker.Widgets, a COM server reading widget.json), which depends on Windows App Runtime 1.8.
-Register installs the built layout on this PC for testing (Windows developer mode required); -Unregister removes it.
Requires the Windows SDK (makeappx.exe).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$IdentityName = 'Aleqsd.CodexTracker',
    [string]$Publisher = 'CN=Aleqsd',
    [string]$PublisherDisplayName = 'Aleqsd',
    [switch]$Register,
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$identityFile = Join-Path $HOME '.codex-tracker/store-identity.json'
if (Test-Path $identityFile) {
    $identity = Get-Content -LiteralPath $identityFile -Raw | ConvertFrom-Json
    if (-not $PSBoundParameters.ContainsKey('IdentityName') -and $identity.IdentityName) { $IdentityName = $identity.IdentityName }
    if (-not $PSBoundParameters.ContainsKey('Publisher') -and $identity.Publisher) { $Publisher = $identity.Publisher }
    if (-not $PSBoundParameters.ContainsKey('PublisherDisplayName') -and $identity.PublisherDisplayName) { $PublisherDisplayName = $identity.PublisherDisplayName }
}
if (-not $Version) { [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw; $Version = [string]$props.Project.PropertyGroup.Version }
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "Version stable attendue (x.y.z), reçu $Version." }
$packageVersion = "$Version.0" # The Store requires a zero revision.
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
if (-not $makeappx) { throw 'makeappx.exe introuvable : installer le SDK Windows.' }

$published = Join-Path $root "artifacts/publish/$Version"
if (-not (Test-Path (Join-Path $published 'CodexTracker.exe'))) { & (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version; if ($LASTEXITCODE) { throw 'Publication échouée.' } }
$work = Join-Path $root "artifacts/msix/$Version"
$layout = Join-Path $work 'layout'
if (Test-Path $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item -Path (Join-Path $published '*') -Destination $layout -Recurse
# Widget provider: self-contained next to the tracker (the tracker's single file cannot host it).
$dotnet = & (Join-Path $PSScriptRoot 'find-dotnet.ps1')
& $dotnet publish (Join-Path $root 'src/CodexTracker.Widgets/CodexTracker.Widgets.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -o (Join-Path $layout 'Widgets') | Out-Null
if ($LASTEXITCODE) { throw 'Publication du widget échouée.' }
New-Item -ItemType Directory -Force (Join-Path $layout 'Public') | Out-Null
Set-Content (Join-Path $layout 'Public/README.txt') 'Codex Tracker widget' -Encoding utf8

# Tile and Store images from the 256 px frame of the application icon, on a transparent background.
Add-Type -AssemblyName System.Drawing
$assets = New-Item -ItemType Directory -Force (Join-Path $layout 'Assets')
$icon = [System.Drawing.Icon]::new((Join-Path $root 'src/CodexTracker.App/Assets/App.ico'), 256, 256)
$source = $icon.ToBitmap()
function Save-Logo([string]$name, [int]$width, [int]$height, [double]$scale) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $size = [int][Math]::Round([Math]::Min($width, $height) * $scale)
    $graphics.DrawImage($source, [int](($width - $size) / 2), [int](($height - $size) / 2), $size, $size)
    $graphics.Dispose(); $bitmap.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
}
try {
    Save-Logo 'StoreLogo.png' 50 50 1.0
    Save-Logo 'Square44x44Logo.png' 44 44 1.0
    Save-Logo 'Square44x44Logo.targetsize-256_altform-unplated.png' 256 256 1.0
    Save-Logo 'Square150x150Logo.png' 150 150 0.62
    Save-Logo 'Wide310x150Logo.png' 310 150 0.62
    Save-Logo 'Square310x310Logo.png' 310 310 0.62
}
finally { $source.Dispose(); $icon.Dispose() }
Copy-Item (Join-Path $root 'docs/pill.png') (Join-Path $assets 'WidgetScreenshot.png')

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
         xmlns:desktop6="http://schemas.microsoft.com/appx/manifest/desktop/windows10/6"
         xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap uap3 desktop desktop6 com rescap">
  <Identity Name="$IdentityName" Publisher="$Publisher" Version="$packageVersion" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Codex Tracker</DisplayName>
    <PublisherDisplayName>$PublisherDisplayName</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" />
    <PackageDependency Name="Microsoft.WindowsAppRuntime.1.8" MinVersion="8000.994.2142.0" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />
  </Dependencies>
  <Resources>
    <Resource Language="en-US" />
    <Resource Language="fr-FR" />
    <Resource Language="es-ES" />
    <Resource Language="de-DE" />
    <Resource Language="pt-BR" />
    <Resource Language="ja-JP" />
  </Resources>
  <Applications>
    <Application Id="CodexTracker" Executable="CodexTracker.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Codex Tracker" Description="Codex and Claude Code usage limits in the Windows taskbar"
                          BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" Square310x310Logo="Assets\Square310x310Logo.png" />
      </uap:VisualElements>
      <Extensions>
        <uap3:Extension Category="windows.appExecutionAlias" Executable="CodexTracker.exe" EntryPoint="Windows.FullTrustApplication">
          <uap3:AppExecutionAlias>
            <desktop:ExecutionAlias Alias="codex-tracker.exe" />
          </uap3:AppExecutionAlias>
        </uap3:Extension>
        <com:Extension Category="windows.comServer">
          <com:ComServer>
            <com:ExeServer Executable="Widgets\CodexTracker.Widgets.exe" Arguments="-RegisterProcessAsComServer" DisplayName="Codex Tracker widget">
              <com:Class Id="6E0B7C2A-9A4D-4F6B-9C2E-3D1A7B5E8F40" DisplayName="Codex Tracker quotas" />
            </com:ExeServer>
          </com:ComServer>
        </com:Extension>
        <uap3:Extension Category="windows.appExtension">
          <uap3:AppExtension Name="com.microsoft.windows.widgets" DisplayName="Codex Tracker" Id="CodexTrackerWidgets" PublicFolder="Public">
            <uap3:Properties>
              <WidgetProvider>
                <ProviderIcons><Icon Path="Assets\StoreLogo.png" /></ProviderIcons>
                <Activation><CreateInstance ClassId="6E0B7C2A-9A4D-4F6B-9C2E-3D1A7B5E8F40" /></Activation>
                <Definitions>
                  <Definition Id="quotas" DisplayName="Codex Tracker" Description="Codex and Claude Code usage left, next reset and the account to switch to" AllowMultiple="false" IsCustomizable="false">
                    <Capabilities>
                      <Capability><Size Name="small" /></Capability>
                      <Capability><Size Name="medium" /></Capability>
                      <Capability><Size Name="large" /></Capability>
                    </Capabilities>
                    <ThemeResources>
                      <Icons><Icon Path="Assets\StoreLogo.png" /></Icons>
                      <Screenshots><Screenshot Path="Assets\WidgetScreenshot.png" DisplayAltText="Codex Tracker quotas widget" /></Screenshots>
                      <DarkMode />
                      <LightMode />
                    </ThemeResources>
                  </Definition>
                </Definitions>
              </WidgetProvider>
            </uap3:Properties>
          </uap3:AppExtension>
        </uap3:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <rescap:Capability Name="unvirtualizedResources" />
  </Capabilities>
</Package>
"@
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
$output = Join-Path $root "artifacts/CodexTracker-$Version.msix"
& $makeappx.FullName pack /d $layout /p $output /o /h SHA256 | Out-Null
if ($LASTEXITCODE) { throw 'makeappx a échoué.' }
Write-Output "Paquet Store $packageVersion : $output ($([Math]::Round((Get-Item $output).Length / 1MB, 1)) Mo)"
if ($Unregister) { Get-AppxPackage -Name $IdentityName | Remove-AppxPackage -ErrorAction SilentlyContinue }
if ($Register) {
    # Loose layout registration: developer mode only, nothing signed, removed with -Unregister.
    # Re-registering in place keeps the widgets already pinned on the board.
    try { Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceApplicationShutdown -ForceUpdateFromAnyVersion }
    catch [System.Exception] {
        if ($_.Exception.HResult -ne -2147009285 -and $_.Exception.Message -notmatch '0x80073CFB') { throw }
        # Same version already registered from this folder: its files were just replaced in place.
        Write-Output 'Déjà inscrit : fichiers mis à jour sur place. Pour un manifeste modifié : -Unregister puis -Register.'
    }
    Write-Output "Paquet de test installé : Win+W → Ajouter des widgets → Codex Tracker."
}
