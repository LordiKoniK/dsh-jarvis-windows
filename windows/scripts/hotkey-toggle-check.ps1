# Presses the panel's global hotkey with SendInput (a real keyboard event, so it
# goes through RegisterHotKey/WM_HOTKEY rather than depending on which window has
# focus), then reports whether the quick bar window came up.
#
# The bar is identified by being the only visible window of the process that is
# not the orb: it is a fixed 320pt-wide topmost window with no frame.

param(
    [switch]$Toggle,
    [int]$SettleMs = 1200
)

$ErrorActionPreference = 'Stop'

Add-Type -Namespace JarvisHotKey -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder text, int count);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$KEYEVENTF_KEYUP = 0x0002
$VK_CONTROL = 0x11
$VK_MENU = 0x12
$VK_J = 0x4A

function Send-Chord {
    $inputs = New-Object 'JarvisHotKey.Native+INPUT[]' 6
    $codes = @($VK_CONTROL, $VK_MENU, $VK_J, $VK_J, $VK_MENU, $VK_CONTROL)
    for ($i = 0; $i -lt 6; $i++) {
        $inputs[$i].type = 1
        $inputs[$i].ki.wVk = $codes[$i]
        $inputs[$i].ki.dwFlags = if ($i -ge 3) { $KEYEVENTF_KEYUP } else { 0 }
    }
    $size = [System.Runtime.InteropServices.Marshal]::SizeOf([type]'JarvisHotKey.Native+INPUT')
    $sent = [JarvisHotKey.Native]::SendInput(6, $inputs, $size)
    Write-Output ("SendInput sent {0} of 6 events (size {1})" -f $sent, $size)
}

function Get-PanelWindows {
    $procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return @() }
    $pids = @($procs | ForEach-Object { [uint32]$_.Id })
    $found = New-Object System.Collections.ArrayList
    $callback = [JarvisHotKey.Native+EnumProc]{
        param([IntPtr]$hwnd, [IntPtr]$lparam)
        $windowPid = 0
        [void][JarvisHotKey.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
        if (($pids -contains $windowPid) -and [JarvisHotKey.Native]::IsWindowVisible($hwnd)) {
            $rect = New-Object JarvisHotKey.Native+RECT
            [void][JarvisHotKey.Native]::GetWindowRect($hwnd, [ref]$rect)
            $class = New-Object System.Text.StringBuilder 256
            [void][JarvisHotKey.Native]::GetClassNameW($hwnd, $class, 256)
            [void]$found.Add([pscustomobject]@{
                Hwnd = ('0x{0:X}' -f [int64]$hwnd)
                Class = $class.ToString()
                Rect = ("{0},{1} {2}x{3}" -f $rect.Left, $rect.Top, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
                Area = ($rect.Right - $rect.Left) * ($rect.Bottom - $rect.Top)
            })
        }
        return $true
    }
    [void][JarvisHotKey.Native]::EnumWindows($callback, [IntPtr]::Zero)
    return $found
}

$before = Get-PanelWindows
Write-Output '--- panel windows before ---'
$before | Format-Table -AutoSize

Send-Chord
Start-Sleep -Milliseconds $SettleMs

$after = Get-PanelWindows
Write-Output '--- panel windows after ---'
$after | Format-Table -AutoSize

$bar = @($after | Sort-Object Area -Descending | Select-Object -Skip 1)
Write-Output ("quick bar visible: {0}" -f ($bar.Count -gt 0))
foreach ($window in $bar) { Write-Output ("  bar window {0} at {1}" -f $window.Hwnd, $window.Rect) }
