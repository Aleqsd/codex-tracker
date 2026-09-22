# Windows PowerShell 5.1 supplies the desktop UI Automation assemblies without
# adding a runtime dependency to the released app. Called only by the fresh-profile
# validation script, never against an installed user's tracker.
param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$ProcessId,
    [ValidateSet('Observe', 'Invoke')][string]$Mode = 'Observe'
)
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ buttonVisible = $false; buttonEnabled = $false; invoked = $false; error = $null }
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName WindowsBase
    $target = Get-Process -Id $ProcessId
    if ($target.ProcessName -ne 'CodexTracker') { throw 'unexpected_process' }
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'UpdateNowButton')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))
    )
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    $button = $null
    do {
        $target.Refresh()
        if ($target.HasExited) { throw 'process_exited' }
        if ($target.MainWindowHandle -ne [IntPtr]::Zero) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($target.MainWindowHandle)
            $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $button) {
                $current = $button.Current
                $result.buttonVisible = !$current.IsOffscreen -and $current.BoundingRectangle.Width -gt 0 -and $current.BoundingRectangle.Height -gt 0
                $result.buttonEnabled = $current.IsEnabled
                if ($result.buttonVisible -and $result.buttonEnabled) { break }
            }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$result.buttonVisible -or !$result.buttonEnabled) { throw 'button_unavailable' }
    if ($Mode -eq 'Invoke') {
        $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
        $result.invoked = $true
    }
} catch {
    # Never copy arbitrary UI text, process paths or exception messages to public CI.
    $result.error = 'update_button_probe_failed'
}
$result | ConvertTo-Json -Compress
if ($result.error) { exit 1 }
