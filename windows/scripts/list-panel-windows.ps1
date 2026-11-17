# Lists every top-level window the panel owns (visible or not) with its class,
# title, ex-style and rect. The quick bar is the only panel window that is not the
# orb, so a click on the orb can be judged from whether that window turned visible.

$ErrorActionPreference = 'Stop'
Add-Type -Namespace JarvisWindows -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder text, int count);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Output 'jarvis-panel is not running'; exit 2 }
$pids = @($procs | ForEach-Object { [uint32]$_.Id })

$found = New-Object System.Collections.ArrayList
$callback = [JarvisWindows.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $windowPid = 0
    [void][JarvisWindows.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($pids -contains $windowPid) {
        $rect = New-Object JarvisWindows.Native+RECT
        [void][JarvisWindows.Native]::GetWindowRect($hwnd, [ref]$rect)
        $class = New-Object System.Text.StringBuilder 256
        [void][JarvisWindows.Native]::GetClassNameW($hwnd, $class, 256)
        $title = New-Object System.Text.StringBuilder 256
        [void][JarvisWindows.Native]::GetWindowTextW($hwnd, $title, 256)
        [void]$found.Add([pscustomobject]@{
            Hwnd = ('0x{0:X}' -f [int64]$hwnd)
            Visible = [JarvisWindows.Native]::IsWindowVisible($hwnd)
            Title = $title.ToString()
            Class = $class.ToString()
            ExStyle = ('0x{0:X8}' -f [JarvisWindows.Native]::GetWindowLong($hwnd, -20))
            Rect = ("{0},{1} {2}x{3}" -f $rect.Left, $rect.Top, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
        })
    }
    return $true
}
[void][JarvisWindows.Native]::EnumWindows($callback, [IntPtr]::Zero)

$found | Format-Table Hwnd, Visible, Title, Rect, ExStyle -AutoSize
Write-Output ''
Write-Output '--- classes ---'
$found | Group-Object Class | Select-Object Count, Name | Format-Table -AutoSize
