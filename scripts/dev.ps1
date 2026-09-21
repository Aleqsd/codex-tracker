param(
    [ValidateSet('Check','Demo','Preview')][string]$Action = 'Check',
    [string]$Dotnet = 'dotnet',
    [string]$Python = 'python',
    [ValidateSet('Comptes','Resets','Semaine','Général','Rappels','Canaux','Historique','Calendrier','Assistants','Application')][string]$View = 'Comptes',
    [ValidateSet('light','dark')][string]$Theme = 'dark',
    [ValidateSet('normal','compact')][string]$Size = 'normal',
    [ValidateSet(96,144,192)][int]$Dpi = 96,
    [switch]$Matrix
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & $Dotnet build CodexTracker.slnx -c Release
    if ($LASTEXITCODE) { throw 'Compilation échouée.' }
    if ($Action -eq 'Check') {
        & $Dotnet test tests/CodexTracker.Tests -c Release --no-build
        if ($LASTEXITCODE) { throw 'Tests métier échoués.' }
        & $Dotnet run --project tests/CodexTracker.UiSmoke -c Release --no-build
        if ($LASTEXITCODE) { throw 'Tests WPF échoués.' }
        # The framework-dependent developer exe needs the SDK's runtime on machines without a system install.
        $dotnetCommand = (Get-Command $Dotnet).Source
        $env:DOTNET_ROOT = Split-Path -Parent $dotnetCommand
        & $Python scripts/test-mcp.py (Join-Path $root 'src/CodexTracker.App/bin/Release/net10.0-windows/CodexTracker.exe')
        if ($LASTEXITCODE) { throw 'Tests MCP échoués.' }
    } elseif ($Action -eq 'Demo') {
        & $Dotnet run --project src/CodexTracker.App -c Release --no-build -- --demo --theme $Theme
    } else {
        $views = if ($Matrix) { @('Comptes','Resets','Semaine','Assistants') } else { @($View) }
        $themes = if ($Matrix) { @('light','dark') } else { @($Theme) }
        $sizes = if ($Matrix) { @('normal','compact') } else { @($Size) }
        $dpis = if ($Matrix) { @(96,144,192) } else { @($Dpi) }
        foreach ($v in $views) { foreach ($t in $themes) { foreach ($s in $sizes) { foreach ($d in $dpis) {
            $output = Join-Path $root "artifacts/previews/$v-$t-$s-$d.png"
            & $Dotnet run --project src/CodexTracker.App -c Release --no-build -- --demo --preview $output --view $v --theme $t --size $s --dpi $d
            if ($LASTEXITCODE -or !(Test-Path -LiteralPath $output)) { throw "Aperçu échoué : $output" }
            Write-Output $output
        } } } }
    }
} finally { Pop-Location }
