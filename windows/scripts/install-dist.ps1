# Copies a staged build into windows\dist.
#
# The host plugin supervises dist\jarvis-panel.exe and respawns it about a second
# after it dies, which keeps the file locked, so the copy is retried against a
# freshly killed process until it wins the race (or the supervisor's crash limit
# stops the respawning).
param(
    [string]$Stage = "$env:TEMP\jarvis-stage",
    [string]$Dest = (Join-Path $PSScriptRoot '..\dist'),
    [int]$Attempts = 6
)

$ErrorActionPreference = 'Continue'
$names = @('jarvis-panel.exe', 'jarvis-panel.dll', 'jarvis-panel.pdb', 'jarvis-panel.deps.json', 'jarvis-panel.runtimeconfig.json')

for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    Get-Process jarvis-panel -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Milliseconds 250

    $failed = $false
    foreach ($name in $names) {
        $source = Join-Path $Stage $name
        if (-not (Test-Path $source)) { continue }
        try {
            Copy-Item $source (Join-Path $Dest $name) -Force -ErrorAction Stop
        }
        catch {
            Write-Output ("attempt {0}: {1} is locked" -f $attempt, $name)
            $failed = $true
            break
        }
    }

    if (-not $failed) {
        Write-Output ("installed into {0} on attempt {1}" -f $Dest, $attempt)
        exit 0
    }
    Start-Sleep -Seconds 1
}

Write-Output 'install failed: the exe stayed locked'
exit 1
