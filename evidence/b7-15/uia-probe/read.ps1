#Requires -Version 7
param([int]$ProcessId)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE=[System.Windows.Automation.AutomationElement]
Start-Sleep 4
$win = $AE::RootElement.FindFirst('Children', [System.Windows.Automation.PropertyCondition]::new($AE::ProcessIdProperty, $ProcessId))
function Dump($el, $depth, $walker) {
  $c = $walker.GetFirstChild($el)
  while ($c) { "{0}{1} id='{2}' name='{3}' status='{4}' class='{5}'" -f ('  '*$depth), $c.Current.ControlType.ProgrammaticName, $c.Current.AutomationId, $c.Current.Name, $c.Current.ItemStatus, $c.Current.ClassName; Dump $c ($depth+1) $walker; $c = $walker.GetNextSibling($c) }
}
foreach ($id in 'WorklistItems','JourneyPlanLegs') {
  $el = $win.FindFirst('Descendants', [System.Windows.Automation.PropertyCondition]::new($AE::AutomationIdProperty, $id))
  "== $id (control view)"; Dump $el 1 ([System.Windows.Automation.TreeWalker]::ControlViewWalker)
  "== $id (raw view)"; Dump $el 1 ([System.Windows.Automation.TreeWalker]::RawViewWalker)
}
