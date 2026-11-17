# Drives the tray icon through UI Automation, which is the same path a real click
# takes: it finds the panel's notification-area button by name, invokes it and
# reports whether the quick bar window appeared.
#
# Used instead of a synthesized keystroke because the global hotkey path can be
# filtered by whichever application has focus, while the tray button is always
# reachable.

param(
    [string]$Name = 'Jarvis panel',
    [int]$SettleMs = 1500
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$automation = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$button = $automation.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)

if ($null -eq $button) {
    Write-Output ("no notification-area button named '{0}'" -f $Name)
    exit 2
}

Write-Output ("found tray button: name='{0}' class='{1}' type='{2}'" -f `
    $button.Current.Name, $button.Current.ClassName, $button.Current.ControlType.ProgrammaticName)

$pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$pattern.Invoke()
Write-Output 'invoked the tray button'
Start-Sleep -Milliseconds $SettleMs
exit 0
