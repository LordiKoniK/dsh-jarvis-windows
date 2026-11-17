# Watches the panel's quick bar window for a few seconds after a gesture, sampling
# IsWindowVisible every 200 ms. A bar that "flashes and vanishes" shows up here as
# a visible sample followed by invisible ones, together with the moment it went.

param(
    [ValidateSet('none', 'orb', 'hotkey')][string]$Gesture = 'orb',
    [int]$Seconds = 6,
    [int]$IntervalMs = 200
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace JarvisWatch -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct INPUTMOUSE { public uint type; public MOUSEINPUT mi; }
[StructLayout(LayoutKind.Sequential)] public struct INPUTKEY { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }
[DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUTMOUSE[] pInputs, int cbSize);
[DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUTKEY[] pInputs, int cbSize);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$MOUSEEVENTF_LEFTDOWN = 0x0002
$MOUSEEVENTF_LEFTUP = 0x0004
$KEYEVENTF_KEYUP = 0x0002

function Get-PanelWindows {
    $procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return @() }
    $pids = @($procs | ForEach-Object { [uint32]$_.Id })
    $found = New-Object System.Collections.ArrayList
    $callback = [JarvisWatch.Native+EnumProc]{
        param([IntPtr]$hwnd, [IntPtr]$lparam)
        $windowPid = 0
        [void][JarvisWatch.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
        if ($pids -contains $windowPid) {
            $rect = New-Object JarvisWatch.Native+RECT
            [void][JarvisWatch.Native]::GetWindowRect($hwnd, [ref]$rect)
            [void]$found.Add([pscustomobject]@{
                Hwnd = $hwnd
                Visible = [JarvisWatch.Native]::IsWindowVisible($hwnd)
                Left = $rect.Left
                Top = $rect.Top
                Width = $rect.Right - $rect.Left
                Height = $rect.Bottom - $rect.Top
            })
        }
        return $true
    }
    [void][JarvisWatch.Native]::EnumWindows($callback, [IntPtr]::Zero)
    return $found
}

# The quick bar is the panel window that is 320 px wide and not the orb.
function Get-BarWindow {
    return Get-PanelWindows | Where-Object { $_.Width -eq 320 -and $_.Height -lt 400 } | Select-Object -First 1
}

$orb = Get-PanelWindows | Where-Object { $_.Width -eq 140 } | Select-Object -First 1
if (-not $orb) { Write-Output 'no orb window found'; exit 2 }

switch ($Gesture) {
    'orb' {
        $x = $orb.Left + 70
        $y = $orb.Top + 70
        [void][JarvisWatch.Native]::SetCursorPos($x, $y)
        Start-Sleep -Milliseconds 500
        $size = [System.Runtime.InteropServices.Marshal]::SizeOf([type]'JarvisWatch.Native+INPUTMOUSE')
        $inputs = New-Object 'JarvisWatch.Native+INPUTMOUSE[]' 2
        $inputs[0].type = 0; $inputs[0].mi.dwFlags = $MOUSEEVENTF_LEFTDOWN
        $inputs[1].type = 0; $inputs[1].mi.dwFlags = $MOUSEEVENTF_LEFTUP
        [void][JarvisWatch.Native]::SendInput(2, $inputs, $size)
        Write-Output ("gesture: clicked orb at {0},{1}" -f $x, $y)
    }
    'hotkey' {
        $size = [System.Runtime.InteropServices.Marshal]::SizeOf([type]'JarvisWatch.Native+INPUTKEY')
        $inputs = New-Object 'JarvisWatch.Native+INPUTKEY[]' 6
        $codes = @(0x11, 0x12, 0x4A, 0x4A, 0x12, 0x11)
        for ($i = 0; $i -lt 6; $i++) {
            $inputs[$i].type = 1
            $inputs[$i].ki.wVk = $codes[$i]
            $inputs[$i].ki.dwFlags = if ($i -ge 3) { $KEYEVENTF_KEYUP } else { 0 }
        }
        [void][JarvisWatch.Native]::SendInput(6, $inputs, $size)
        Write-Output 'gesture: pressed Ctrl+Alt+J'
    }
    default { Write-Output 'gesture: none (observing only)' }
}

$samples = [Math]::Ceiling($Seconds * 1000 / $IntervalMs)
$last = $null
for ($i = 0; $i -lt $samples; $i++) {
    Start-Sleep -Milliseconds $IntervalMs
    $bar = Get-BarWindow
    $state = if ($bar) { "visible=$($bar.Visible) rect=$($bar.Left),$($bar.Top) $($bar.Width)x$($bar.Height)" } else { 'no bar window' }
    if ($state -ne $last) {
        Write-Output ("{0,6:N0} ms  {1}" -f ($i * $IntervalMs), $state)
        $last = $state
    }
}

