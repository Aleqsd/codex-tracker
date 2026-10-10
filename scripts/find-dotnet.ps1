# Returns a dotnet executable with a .NET 10 SDK: the one on PATH, else the per-user install made by dotnet-install.ps1.
$candidates = @(
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
)
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate) -and (& $candidate --list-sdks | Where-Object { $_ -match '^10\.' })) { return $candidate }
}
throw "SDK .NET 10 introuvable : l’installer (https://dot.net/v1/dotnet-install.ps1 -Channel 10.0) ou passer -Dotnet."
