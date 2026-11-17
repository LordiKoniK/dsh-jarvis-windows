# Lists the notification-area buttons UI Automation can see, so the panel's tray
# icon can be found (and clicked) without guessing its position.
#
# Windows 11 hides most tray icons in the overflow flyout; an icon that is not in
# this list is in the overflow and has to be revealed before it can be invoked.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$automation = [System.Windows.Automation.AutomationElement]::RootElement
$all = $automation.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

Write-Output ("elements: {0}" -f $all.Count)
Write-Output ''

foreach ($element in $all) {
    try {
        $name = $element.Current.Name
        $class = $element.Current.ClassName
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($class -notmatch 'Tray|Notify|Toolbar|Button') { continue }
        Write-Output ("name='{0}' class='{1}' type='{2}'" -f $name, $class, $element.Current.ControlType.ProgrammaticName)
    } catch {
        # The tray is rebuilt constantly; an element can vanish mid-enumeration.
    }
}

Write-Output ''
Write-Output '--- anything whose name mentions jarvis ---'
foreach ($element in $all) {
    try {
        if ($element.Current.Name -match 'jarvis') {
            Write-Output ("name='{0}' class='{1}' type='{2}' enabled={3}" -f `
                $element.Current.Name, $element.Current.ClassName, `
                $element.Current.ControlType.ProgrammaticName, $element.Current.IsEnabled)
        }
    } catch {
    }
}
