# Moves the quick bar to an empty part of the desktop and leaves it there, so its
# rendering can be captured without another window underneath it.
#
# Used to separate a real paint bug from a capture artifact: a topmost window with a
# topmost neighbour underneath produces ghosted pixels in some screen grabs.

param(
    [int]$X = 200,
    [int]$Y = 700
)

$ErrorActionPreference = 'Stop'
Add-Type -Namespace JarvisMove -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Output 'jarvis-panel is not running'; exit 2 }
$pids = @($procs | ForEach-Object { [uint32]$_.Id })

$script:bar = [IntPtr]::Zero
$callback = [JarvisMove.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $windowPid = 0
    [void][JarvisMove.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($pids -contains $windowPid) {
        $rect = New-Object JarvisMove.Native+RECT
        [void][JarvisMove.Native]::GetWindowRect($hwnd, [ref]$rect)
        if ((($rect.Right - $rect.Left) -eq 320) -and [JarvisMove.Native]::IsWindowVisible($hwnd)) { $script:bar = $hwnd }
    }
    return $true
}
[void][JarvisMove.Native]::EnumWindows($callback, [IntPtr]::Zero)

if ($script:bar -eq [IntPtr]::Zero) { Write-Output 'no visible quick bar window'; exit 2 }
# SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW
$ok = [JarvisMove.Native]::SetWindowPos($script:bar, [IntPtr]::Zero, $X, $Y, 0, 0, 0x0001 -bor 0x0010 -bor 0x0040)
Write-Output ("moved bar 0x{0:X} to {1},{2}: {3}" -f [int64]$script:bar, $X, $Y, $ok)
