# Enumerates the child windows of the panel's quick bar with their text, bounds and
# visibility. This is the ground truth about what the window contains, independent
# of what a screenshot appears to show.

$ErrorActionPreference = 'Stop'
Add-Type -Namespace JarvisKids -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder text, int count);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Output 'jarvis-panel is not running'; exit 2 }
$pids = @($procs | ForEach-Object { [uint32]$_.Id })

$script:bar = [IntPtr]::Zero
$top = [JarvisKids.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $windowPid = 0
    [void][JarvisKids.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($pids -contains $windowPid) {
        $rect = New-Object JarvisKids.Native+RECT
        [void][JarvisKids.Native]::GetWindowRect($hwnd, [ref]$rect)
        if ((($rect.Right - $rect.Left) -eq 320) -and [JarvisKids.Native]::IsWindowVisible($hwnd)) { $script:bar = $hwnd }
    }
    return $true
}
[void][JarvisKids.Native]::EnumWindows($top, [IntPtr]::Zero)

if ($script:bar -eq [IntPtr]::Zero) { Write-Output 'no visible 320px panel window'; exit 2 }
$barRect = New-Object JarvisKids.Native+RECT
[void][JarvisKids.Native]::GetWindowRect($script:bar, [ref]$barRect)
Write-Output ("bar 0x{0:X} at {1},{2} {3}x{4}" -f [int64]$script:bar, $barRect.Left, $barRect.Top,
    ($barRect.Right - $barRect.Left), ($barRect.Bottom - $barRect.Top))
Write-Output ''

$script:rows = New-Object System.Collections.ArrayList
$kids = [JarvisKids.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $rect = New-Object JarvisKids.Native+RECT
    [void][JarvisKids.Native]::GetWindowRect($hwnd, [ref]$rect)
    $text = New-Object System.Text.StringBuilder 256
    [void][JarvisKids.Native]::GetWindowTextW($hwnd, $text, 256)
    $class = New-Object System.Text.StringBuilder 256
    [void][JarvisKids.Native]::GetClassNameW($hwnd, $class, 256)
    [void]$script:rows.Add([pscustomobject]@{
        RelX = $rect.Left - $barRect.Left
        RelY = $rect.Top - $barRect.Top
        Width = $rect.Right - $rect.Left
        Height = $rect.Bottom - $rect.Top
        Visible = [JarvisKids.Native]::IsWindowVisible($hwnd)
        Text = $text.ToString()
        Class = $class.ToString()
    })
    return $true
}
[void][JarvisKids.Native]::EnumChildWindows($script:bar, $kids, [IntPtr]::Zero)

$script:rows | Sort-Object RelY, RelX | Format-Table -AutoSize
Write-Output ("child windows: {0}" -f $script:rows.Count)
$texts = $script:rows | Where-Object { $_.Text -ne '' } | ForEach-Object { $_.Text }
Write-Output ("texts: " + ($texts -join ' | '))
