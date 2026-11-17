# Focus-behaviour audit for the running orb: enumerates the panel's own windows,
# prints their class/title/ex-style and reports whether the orb kept
# WS_EX_NOACTIVATE and whether it is (or ever became) the foreground window.
#
# Read-only: it never activates, moves or closes anything.

$ErrorActionPreference = 'Stop'

Add-Type -Namespace JarvisAudit -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder text, int count);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$WS_EX_NOACTIVATE = 0x08000000
$WS_EX_TOOLWINDOW = 0x00000080
$WS_EX_LAYERED    = 0x00080000
$GWL_EXSTYLE      = -20

$panel = Get-Process jarvis-panel -ErrorAction SilentlyContinue
if (-not $panel) { Write-Output 'NO PROCESS: jarvis-panel is not running'; exit 2 }

$pids = @($panel | ForEach-Object { [uint32]$_.Id })
Write-Output ("jarvis-panel pids: " + ($pids -join ', '))

$foreground = [JarvisAudit.Native]::GetForegroundWindow()
$fgPid = 0
if ($foreground -ne [IntPtr]::Zero) { [void][JarvisAudit.Native]::GetWindowThreadProcessId($foreground, [ref]$fgPid) }
Write-Output ("foreground hwnd=0x{0:X} pid={1} {2}" -f [int64]$foreground, $fgPid, (Get-Process -Id $fgPid -ErrorAction SilentlyContinue).ProcessName)
Write-Output ("orb is foreground: " + ($pids -contains $fgPid))
Write-Output ''

$rows = New-Object System.Collections.ArrayList
$callback = [JarvisAudit.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $windowPid = 0
    [void][JarvisAudit.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($pids -contains $windowPid) {
        $title = New-Object System.Text.StringBuilder 256
        [void][JarvisAudit.Native]::GetWindowTextW($hwnd, $title, 256)
        $class = New-Object System.Text.StringBuilder 256
        [void][JarvisAudit.Native]::GetClassNameW($hwnd, $class, 256)
        $ex = [JarvisAudit.Native]::GetWindowLong($hwnd, $GWL_EXSTYLE)
        $rect = New-Object JarvisAudit.Native+RECT
        [void][JarvisAudit.Native]::GetWindowRect($hwnd, [ref]$rect)
        [void]$rows.Add([pscustomobject]@{
            Hwnd     = ('0x{0:X}' -f [int64]$hwnd)
            Class    = $class.ToString()
            Title    = $title.ToString()
            Visible  = [JarvisAudit.Native]::IsWindowVisible($hwnd)
            NoAct    = (($ex -band $WS_EX_NOACTIVATE) -ne 0)
            ToolWin  = (($ex -band $WS_EX_TOOLWINDOW) -ne 0)
            Layered  = (($ex -band $WS_EX_LAYERED) -ne 0)
            ExStyle  = ('0x{0:X8}' -f $ex)
            Rect     = ("{0},{1} {2}x{3}" -f $rect.Left, $rect.Top, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
        })
    }
    return $true
}
[void][JarvisAudit.Native]::EnumWindows($callback, [IntPtr]::Zero)

$rows | Format-Table -AutoSize

# The orb is the only window of the process that draws and shows itself; every
# other entry above is a hidden WinForms/IME helper with no title.
$orb = $rows | Where-Object { $_.Visible -and $_.Title -eq 'Jarvis' }
Write-Output ''
foreach ($window in $orb) {
    Write-Output ("orb window {0} '{1}' visible={2} exStyle={3} layered={4} toolwindow={5} noactivate={6}" -f `
        $window.Hwnd, $window.Title, $window.Visible, $window.ExStyle, $window.Layered, $window.ToolWin, $window.NoAct)
}
$noActivate = @($orb | Where-Object { $_.NoAct })
Write-Output ("orb windows found: {0}" -f @($orb).Count)
Write-Output ("orb keeps WS_EX_NOACTIVATE: {0}" -f ($noActivate.Count -gt 0))
Write-Output ("orb is the foreground window: {0}" -f ($pids -contains $fgPid))
if ($noActivate.Count -eq 0) { exit 1 }
exit 0
