# Opens the quick bar, then screenshots the desktop twice: once as the panel left it,
# and once after forcing the bar window to the top of the z-order with SetWindowPos.
#
# This separates "the bar is not drawn" from "the bar is drawn but covered", which
# look identical in a screenshot but have completely different causes.

param(
    [string]$OutDir = $env:TEMP,
    [int]$SettleMs = 1200
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -Namespace JarvisZ -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
[StructLayout(LayoutKind.Sequential)] public struct INPUTKEY { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }
[DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUTKEY[] pInputs, int cbSize);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Save-Shot([string]$path) {
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bitmap.Size)
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Find-Bar {
    $procs = @(Get-Process jarvis-panel -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return $null }
    $pids = @($procs | ForEach-Object { [uint32]$_.Id })
    $script:bar = $null
    $callback = [JarvisZ.Native+EnumProc]{
        param([IntPtr]$hwnd, [IntPtr]$lparam)
        $windowPid = 0
        [void][JarvisZ.Native]::GetWindowThreadProcessId($hwnd, [ref]$windowPid)
        if ($pids -contains $windowPid) {
            $rect = New-Object JarvisZ.Native+RECT
            [void][JarvisZ.Native]::GetWindowRect($hwnd, [ref]$rect)
            if (($rect.Right - $rect.Left) -eq 320) { $script:bar = $hwnd }
        }
        return $true
    }
    [void][JarvisZ.Native]::EnumWindows($callback, [IntPtr]::Zero)
    return $script:bar
}

# Toggle the bar open with the real hotkey.
$size = [System.Runtime.InteropServices.Marshal]::SizeOf([type]'JarvisZ.Native+INPUTKEY')
$inputs = New-Object 'JarvisZ.Native+INPUTKEY[]' 6
$codes = @(0x11, 0x12, 0x4A, 0x4A, 0x12, 0x11)
for ($i = 0; $i -lt 6; $i++) {
    $inputs[$i].type = 1
    $inputs[$i].ki.wVk = $codes[$i]
    $inputs[$i].ki.dwFlags = if ($i -ge 3) { 0x0002 } else { 0 }
}
[void][JarvisZ.Native]::SendInput(6, $inputs, $size)
Start-Sleep -Milliseconds $SettleMs

$bar = Find-Bar
if (-not $bar) { Write-Output 'no quick bar window'; exit 2 }
$rect = New-Object JarvisZ.Native+RECT
[void][JarvisZ.Native]::GetWindowRect($bar, [ref]$rect)
Write-Output ("bar 0x{0:X} visible={1} rect={2},{3} {4}x{5} exStyle=0x{6:X8}" -f `
    [int64]$bar, [JarvisZ.Native]::IsWindowVisible($bar), $rect.Left, $rect.Top,
    ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top), [JarvisZ.Native]::GetWindowLong($bar, -20))

$before = Join-Path $OutDir 'jarvis-z-before.png'
Save-Shot $before
Write-Output ("screenshot before: {0}" -f $before)

# HWND_TOPMOST with SWP_NOMOVE|SWP_NOSIZE|SWP_SHOWWINDOW|SWP_NOACTIVATE
$ok = [JarvisZ.Native]::SetWindowPos($bar, [IntPtr](-1), 0, 0, 0, 0, 0x0002 -bor 0x0001 -bor 0x0040 -bor 0x0010)
Write-Output ("SetWindowPos topmost+show: {0}" -f $ok)
Start-Sleep -Milliseconds 600

$after = Join-Path $OutDir 'jarvis-z-after.png'
Save-Shot $after
Write-Output ("screenshot after: {0}" -f $after)
