# Reports whether a window is really on screen: DWM cloaking, placement show state,
# z-order and monitor. A window can report IsWindowVisible = true and still be
# cloaked or minimised, which looks exactly like "it flashed and vanished".

param(
    [string]$Title = 'Jarvis'
)

$ErrorActionPreference = 'Stop'
Add-Type -Namespace JarvisGhost -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Output 'jarvis-panel is not running'; exit 2 }
$pids = @($procs | ForEach-Object { [uint32]$_.Id })

$found = New-Object System.Collections.ArrayList
$callback = [JarvisGhost.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $windowPid = 0
    [void][JarvisGhost.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($pids -contains $windowPid) {
        $title = New-Object System.Text.StringBuilder 256
        [void][JarvisGhost.Native]::GetWindowTextW($hwnd, $title, 256)
        if ($title.ToString() -eq $Title) {
            $rect = New-Object JarvisGhost.Native+RECT
            [void][JarvisGhost.Native]::GetWindowRect($hwnd, [ref]$rect)
            $cloaked = 0
            [void][JarvisGhost.Native]::DwmGetWindowAttribute($hwnd, 14, [ref]$cloaked, 4)
            [void]$found.Add([pscustomobject]@{
                Hwnd = ('0x{0:X}' -f [int64]$hwnd)
                Visible = [JarvisGhost.Native]::IsWindowVisible($hwnd)
                Iconic = [JarvisGhost.Native]::IsIconic($hwnd)
                Zoomed = [JarvisGhost.Native]::IsZoomed($hwnd)
                Cloaked = $cloaked
                ExStyle = ('0x{0:X8}' -f [JarvisGhost.Native]::GetWindowLong($hwnd, -20))
                Style = ('0x{0:X8}' -f [JarvisGhost.Native]::GetWindowLong($hwnd, -16))
                Prev = ('0x{0:X}' -f [int64][JarvisGhost.Native]::GetWindow($hwnd, 3))
                Monitor = ('0x{0:X}' -f [int64][JarvisGhost.Native]::MonitorFromWindow($hwnd, 2))
                Rect = ("{0},{1} {2}x{3}" -f $rect.Left, $rect.Top, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
            })
        }
    }
    return $true
}
[void][JarvisGhost.Native]::EnumWindows($callback, [IntPtr]::Zero)

$found | Format-List
if ($found.Count -eq 0) { Write-Output ("no top-level window titled '{0}'" -f $Title) }
