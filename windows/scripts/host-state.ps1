# Prints the host snapshot the panel renders from, so a Stage B surface can be
# checked against real data (pending cards, auto-approval entries, targets).
#
# Read-only: GET /jarvis/state only.

$runtimePath = Join-Path $env:USERPROFILE '.dsh\jarvis\runtime.json'
if (-not (Test-Path $runtimePath)) { Write-Output 'no runtime.json: the host is not running'; exit 2 }
$runtime = Get-Content $runtimePath -Raw | ConvertFrom-Json

$headers = @{ Authorization = "Bearer $($runtime.token)" }
if ($runtime.rendererHeader) {
    $headers[$runtime.rendererHeader.name] = $runtime.rendererHeader.value
}

try {
    $state = Invoke-RestMethod -Uri "$($runtime.origin)/jarvis/state" -Headers $headers -TimeoutSec 5
} catch {
    Write-Output ("state request failed: " + $_.Exception.Message)
    exit 1
}

Write-Output ("activity={0} agentId={1} error={2}" -f $state.activity, $state.agentId, $state.error)
Write-Output ("counts: running={0} pending={1} unread={2} failed={3}" -f `
    $state.counts.running, $state.counts.pending, $state.counts.unread, $state.counts.failed)
Write-Output ("voice: speaking={0} muted={1} paused={2} queued={3}" -f `
    $state.voice.speaking, $state.voice.muted, $state.voice.paused, $state.voice.queued)

Write-Output ''
Write-Output ("sessions: {0}" -f @($state.sessions).Count)
foreach ($session in @($state.sessions)) {
    Write-Output ("  id={0} managed={1} status={2} title='{3}'" -f `
        $session.id, $session.managed, $session.status, $session.title)
}

Write-Output ''
Write-Output ("pending: {0}" -f @($state.pending).Count)
foreach ($item in @($state.pending)) {
    Write-Output ("  id={0} kind={1} canAlwaysAllow={2} title='{3}' detail='{4}' choices={5}" -f `
        $item.id, $item.kind, $item.canAlwaysAllow, $item.title, $item.detail, (@($item.choices) -join '|'))
}

Write-Output ''
Write-Output ("autoApprovals: {0}" -f @($state.autoApprovals).Count)
foreach ($entry in @($state.autoApprovals)) {
    Write-Output ("  id={0} tier={1} tool={2} rule={3}" -f `
        $entry.id, $entry.tier, $entry.tool, [bool]$entry.rule)
}
