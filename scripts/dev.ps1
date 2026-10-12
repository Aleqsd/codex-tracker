param(
    [ValidateSet('Check','Demo','Preview')][string]$Action = 'Check',
    [string]$Dotnet,
    [string]$Python = 'python',
    [ValidateSet('Comptes','Resets','Semaine','Général','Rappels','Canaux','Historique','Calendrier','Assistants','Application')][string]$View = 'Comptes',
    [ValidateSet('light','dark')][string]$Theme = 'dark',
    [ValidateSet('normal','compact')][string]$Size = 'normal',
    [ValidateSet(96,120,144,192)][int]$Dpi = 96,
    [switch]$Claude,
    [switch]$ManualReset,
    [switch]$Matrix,
    # Screen for the real WPF test windows: right (default), left, primary or DISPLAYn.
    [ValidatePattern('^(right|left|primary|DISPLAY\d+)$')]
    [string]$TestScreen
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name ffxiv_dx11,ffxiv -ErrorAction SilentlyContinue) {
    throw 'Final Fantasy XIV est en cours : tests UI et aperçus reportés pour préserver le plein écran. La compilation et les tests métier peuvent être lancés séparément ; relancer les vues après la session de jeu.'
}
$root = Split-Path -Parent $PSScriptRoot
if (-not $Dotnet) { $Dotnet = & (Join-Path $PSScriptRoot 'find-dotnet.ps1') }
if ($TestScreen) { $env:CODEX_TRACKER_TEST_SCREEN = $TestScreen }
Push-Location $root
try {
    $demoOptions = @()
    if ($Claude) { $demoOptions += "--demo-claude" }
    if ($ManualReset) { $demoOptions += "--demo-manual-reset" }
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
        & $Python scripts/test-mcp.py (Join-Path $root 'src/CodexTracker.App/bin/Release/net10.0-windows10.0.19041.0/CodexTracker.exe')
        if ($LASTEXITCODE) { throw 'Tests MCP échoués.' }
        & $Python scripts/test-claude.py (Join-Path $root 'src/CodexTracker.App/bin/Release/net10.0-windows10.0.19041.0/CodexTracker.exe')
        if ($LASTEXITCODE) { throw 'Tests du collecteur Claude Code échoués.' }
    } elseif ($Action -eq 'Demo') {
        & $Dotnet run --project src/CodexTracker.App -c Release --no-build -- --demo --theme $Theme @demoOptions
    } else {
        $views = if ($Matrix) { @('Comptes','Resets','Semaine','Assistants') } else { @($View) }
        $themes = if ($Matrix) { @('light','dark') } else { @($Theme) }
        $sizes = if ($Matrix) { @('normal','compact') } else { @($Size) }
        $dpis = if ($Matrix) { @(96,120,144,192) } else { @($Dpi) }
        foreach ($v in $views) { foreach ($t in $themes) { foreach ($s in $sizes) { foreach ($d in $dpis) {
            $output = Join-Path $root "artifacts/previews/$v-$t-$s-$d.png"
            & $Dotnet run --project src/CodexTracker.App -c Release --no-build -- --demo --preview $output --view $v --theme $t --size $s --dpi $d @demoOptions
            if ($LASTEXITCODE -or !(Test-Path -LiteralPath $output)) { throw "Aperçu échoué : $output" }
            Write-Output $output
        } } } }
    }
} finally { Pop-Location }
