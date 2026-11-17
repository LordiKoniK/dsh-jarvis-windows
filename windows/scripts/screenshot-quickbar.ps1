# Opens the quick bar with the registered global hotkey and takes a full-desktop
# screenshot, so the Stage B layout can be looked at without moving the cursor.
#
# SendKeys is used deliberately: the hotkey is the same path a user takes, and it
# exercises the whole chain (RegisterHotKey -> WM_HOTKEY -> UiActions -> the bar).
# Use -Close to send it a second time and take the bar back down.

param(
    [switch]$Close,
    [string]$Shot = (Join-Path $env:TEMP 'jarvis-quickbar.png'),
    [int]$SettleMs = 1500
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

[void][System.Windows.Forms.SendKeys]::SendWait('^%j')
Start-Sleep -Milliseconds $SettleMs

Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output ("screenshot: {0} ({1}x{2})" -f $Shot, $bounds.Width, $bounds.Height)
