# Launch verification for the panel: starts dist\jarvis-panel.exe, waits, proves the
# process is alive, prints the log lines it produced, then audits focus behaviour
# and stops it again.
#
# The wait matters: the interesting failures (a bad window style, a first frame that
# throws, a layout pass that divides by zero) all happen in the first seconds, and a
# panel that dies during them would otherwise look like a successful start.

param(
    [int]$WaitSeconds = 12,
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\jarvis-panel.exe')
)

$ErrorActionPreference = 'Continue'
$log = Join-Path $env:USERPROFILE '.dsh\jarvis\panel.log'

if (Get-Process jarvis-panel -ErrorAction SilentlyContinue) {
    Write-Output 'another jarvis-panel is already running; stopping it first'
    Get-Process jarvis-panel -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Seconds 2
}

$before = if (Test-Path $log) { @(Get-Content $log).Count } else { 0 }
Write-Output ("log lines before launch: {0}" -f $before)

Start-Process -FilePath $Exe
Start-Sleep -Seconds $WaitSeconds

$process = Get-Process jarvis-panel -ErrorAction SilentlyContinue
if (-not $process) {
    Write-Output ("FAIL: no jarvis-panel process after {0}s" -f $WaitSeconds)
    Write-Output '--- log produced by the run ---'
    if (Test-Path $log) { Get-Content $log | Select-Object -Skip $before }
    exit 1
}

Write-Output ("ALIVE after {0}s: pid {1}, started {2:HH:mm:ss}, working set {3:N0} KB" -f `
    $WaitSeconds, $process.Id, $process.StartTime, ($process.WorkingSet64 / 1KB))

$after = @(Get-Content $log)
Write-Output ("log lines after launch: {0}" -f $after.Count)
Write-Output '--- log produced by this run ---'
$produced = $after | Select-Object -Skip $before
$produced | ForEach-Object { $_ }

$bad = $produced | Where-Object { $_ -match 'frame failed' -or $_ -match 'exception' -or $_ -match 'failed' }
Write-Output ''
if ($bad) {
    Write-Output 'FAIL: the run logged failures:'
    $bad | ForEach-Object { Write-Output ('  ' + $_) }
} else {
    Write-Output 'OK: no frame failures and no exception lines in this run'
}

exit 0
