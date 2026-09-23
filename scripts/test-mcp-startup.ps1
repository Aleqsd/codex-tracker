#requires -Version 7.0
param([Parameter(Mandatory)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exe = [IO.Path]::GetFullPath($ExecutablePath)
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Published executable missing.' }

# Occupy only a unique demonstration pipe, never the installed application's pipe.
# Accept the connection but withhold its greeting to exercise the real 20 s deadline.
$instance = [guid]::NewGuid().ToString('N')
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
$pipeName = "CodexTracker.Mcp.v1.$sid.$session.Demo.$instance"
$pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1,
    [IO.Pipes.PipeTransmissionMode]::Byte, ([IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly))
$process = $null
$deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
try {
    $connection = $pipe.WaitForConnectionAsync($deadline.Token)
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in @('--demo', '--demo-instance', $instance, '--mcp')) { $start.ArgumentList.Add($arg) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $connection.GetAwaiter().GetResult()
    if (!$process.WaitForExit(25000)) { throw 'MCP exceeded its startup deadline.' }
    $elapsed = $watch.Elapsed.TotalSeconds
    $outText = $stdout.GetAwaiter().GetResult(); $errorText = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 1) { throw "Startup timeout must fail explicitly, got exit $($process.ExitCode)." }
    if ($outText.Length -ne 0) { throw 'Startup failure contaminated MCP stdout.' }
    if ($errorText -notmatch '20 secondes' -or $errorText -notmatch 'reconnectez') { throw 'Startup failure lacks actionable stderr.' }
    if ($elapsed -lt 18 -or $elapsed -gt 30) { throw 'Unexpected startup deadline duration.' }
    [ordered]@{ status='passed'; scenario='stalled-demo-pipe'; exitCode=$process.ExitCode;
        stdoutEmpty=$true; actionableError=$true; durationSeconds=[Math]::Round($elapsed, 1);
        personalDataUsed=$false; trackerSettingsChanged=$false } | ConvertTo-Json
}
finally {
    if ($process) {
        if (!$process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
    $deadline.Cancel(); $pipe.Dispose(); $deadline.Dispose()
}
