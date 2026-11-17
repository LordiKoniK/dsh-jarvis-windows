# Clicks the orb with SendInput (absolute cursor move + down/up) and reports the
# panel's visible windows before and after, so "did the quick bar open" can be
# answered without depending on which window has keyboard focus.
#
# This is the same gesture a user makes; it is scripted only because the tray icon
# lives in the Windows 11 overflow flyout.

param(
    [int]$SettleMs = 1500,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'

Add-Type -Namespace JarvisClick -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }
[DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
'@

$MOUSEEVENTF_LEFTDOWN = 0x0002
$MOUSEEVENTF_LEFTUP = 0x0004

function Get-VisiblePanelWindows {
    $procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return @() }
    $pids = @($procs | ForEach-Object { [uint32]$_.Id })
    $found = New-Object System.Collections.ArrayList
    $callback = [JarvisClick.Native+EnumProc]{
        param([IntPtr]$hwnd, [IntPtr]$lparam)
        $windowPid = 0
        [void][JarvisClick.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
        if (($pids -contains $windowPid) -and [JarvisClick.Native]::IsWindowVisible($hwnd)) {
            $rect = New-Object JarvisClick.Native+RECT
            [void][JarvisClick.Native]::GetWindowRect($hwnd, [ref]$rect)
            [void]$found.Add([pscustomobject]@{
                Hwnd = ('0x{0:X}' -f [int64]$hwnd)
                Left = $rect.Left
                Top = $rect.Top
                Width = $rect.Right - $rect.Left
                Height = $rect.Bottom - $rect.Top
            })
        }
        return $true
    }
    [void][JarvisClick.Native]::EnumWindows($callback, [IntPtr]::Zero)
    return $found
}

$before = Get-VisiblePanelWindows
Write-Output '--- visible panel windows before ---'
$before | Format-Table -AutoSize

$orb = $before | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
if (-not $orb) { Write-Output 'no visible panel window to click'; exit 2 }

$x = $orb.Left + [int]($orb.Width / 2)
$y = $orb.Top + [int]($orb.Height / 2)
Write-Output ("clicking orb window {0} at ({1},{2})" -f $orb.Hwnd, $x, $y)

[void][JarvisClick.Native]::SetCursorPos($x, $y)
Start-Sleep -Milliseconds 400

$size = [System.Runtime.InteropServices.Marshal]::SizeOf([type]'JarvisClick.Native+INPUT')
$inputs = New-Object 'JarvisClick.Native+INPUT[]' 2
$inputs[0].type = 0
$inputs[0].mi.dwFlags = $MOUSEEVENTF_LEFTDOWN
$inputs[1].type = 0
$inputs[1].mi.dwFlags = $MOUSEEVENTF_LEFTUP
$sent = [JarvisClick.Native]::SendInput(2, $inputs, $size)
Write-Output ("SendInput sent {0} of 2 events" -f $sent)

Start-Sleep -Milliseconds $SettleMs

$after = Get-VisiblePanelWindows
Write-Output '--- visible panel windows after ---'
$after | Format-Table -AutoSize

$cursor = New-Object JarvisClick.Native+POINT
[void][JarvisClick.Native]::GetCursorPos([ref]$cursor)
$new = @($after | Where-Object { $_.Hwnd -notin @($before.Hwnd) })
Write-Output ("new visible window(s): {0}" -f $new.Count)
foreach ($window in $new) {
    Write-Output ("  {0} at {1},{2} {3}x{4}" -f $window.Hwnd, $window.Left, $window.Top, $window.Width, $window.Height)
}
Write-Output ("cursor now at {0},{1}" -f $cursor.X, $cursor.Y)
