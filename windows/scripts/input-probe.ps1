# Focus and inject-input probe.
#
# Answers two questions with evidence instead of guesswork:
#   1. which window is actually the foreground window, and can a synthetic click
#      reach the panel at all (the orb tracks hover, so a hover that sticks proves
#      the input arrived);
#   2. whether the real global hotkey fires from a plain SendKeys when the target
#      application is the foreground window.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace JarvisProbe -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
'@

function Describe-Window([IntPtr]$Handle) {
    if ($Handle -eq [IntPtr]::Zero) { return 'none' }
    $pid_ = 0
    [void][JarvisProbe.Native]::GetWindowThreadProcessId($Handle, [ref]$pid_)
    $title = New-Object System.Text.StringBuilder 256
    [void][JarvisProbe.Native]::GetWindowTextW($Handle, $title, 256)
    $name = (Get-Process -Id $pid_ -ErrorAction SilentlyContinue).ProcessName
    return ("0x{0:X} '{1}' pid={2} ({3})" -f [int64]$Handle, $title.ToString(), $pid_, $name)
}

Write-Output ("foreground now:      " + (Describe-Window ([JarvisProbe.Native]::GetForegroundWindow())))

# Bring the panel's orb forward the non-activating way, so hotkey tests have a
# deterministic foreground app rather than whatever the console left behind.
$procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Write-Output 'jarvis-panel is not running'; exit 2 }
$pids = @($procs | ForEach-Object { [uint32]$_.Id })

$script:orb = [IntPtr]::Zero
$script:bar = [IntPtr]::Zero
$callback = [JarvisProbe.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $windowPid = 0
    [void][JarvisProbe.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($pids -contains $windowPid) {
        $rect = New-Object JarvisProbe.Native+RECT
        [void][JarvisProbe.Native]::GetWindowRect($hwnd, [ref]$rect)
        if (($rect.Right - $rect.Left) -eq 140) { $script:orb = $hwnd }
        if (($rect.Right - $rect.Left) -eq 320) { $script:bar = $hwnd }
    }
    return $true
}
[void][JarvisProbe.Native]::EnumWindows($callback, [IntPtr]::Zero)

Write-Output ("orb window:          " + (Describe-Window $script:orb))
Write-Output ("bar window:          " + (Describe-Window $script:bar))

$orbRect = New-Object JarvisProbe.Native+RECT
[void][JarvisProbe.Native]::GetWindowRect($script:orb, [ref]$orbRect)
$x = $orbRect.Left + 70
$y = $orbRect.Top + 70

# Hover test: the orb polls the cursor and reports standby while it is over the
# live area, so a cursor parked on the orb proves synthetic input is arriving.
[void][JarvisProbe.Native]::SetCursorPos($x, $y)
Start-Sleep -Milliseconds 800
$cursor = New-Object JarvisProbe.Native+POINT
[void][JarvisProbe.Native]::GetCursorPos([ref]$cursor)
Write-Output ("cursor parked on orb: {0},{1} (wanted {2},{3})" -f $cursor.X, $cursor.Y, $x, $y)

# Hotkey test with SendKeys, from the console, against whatever is foreground.
[void][System.Windows.Forms.SendKeys]::SendWait('^%j')
Start-Sleep -Milliseconds 1500

$script:barVisible = $false
$callback2 = [JarvisProbe.Native+EnumProc]{
    param([IntPtr]$hwnd, [IntPtr]$lparam)
    $windowPid = 0
    [void][JarvisProbe.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
    if ($pids -contains $windowPid) {
        $rect = New-Object JarvisProbe.Native+RECT
        [void][JarvisProbe.Native]::GetWindowRect($hwnd, [ref]$rect)
        if ((($rect.Right - $rect.Left) -eq 320) -and [JarvisProbe.Native]::IsWindowVisible($hwnd)) { $script:barVisible = $true }
    }
    return $true
}
[void][JarvisProbe.Native]::EnumWindows($callback2, [IntPtr]::Zero)
Write-Output ("bar visible after hotkey: {0}" -f $script:barVisible)
Write-Output ("foreground after hotkey:  " + (Describe-Window ([JarvisProbe.Native]::GetForegroundWindow())))
