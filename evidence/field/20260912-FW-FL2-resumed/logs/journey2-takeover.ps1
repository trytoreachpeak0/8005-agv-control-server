#Requires -Version 7
# Damage control: journey afc0ce0a was accepted instead of the charging errand. Load and unload it normally
# so no stop runs into its station deadline and permanently suppresses a real demand.
$ErrorActionPreference = 'Stop'
Import-Module 'C:\Users\szy\Desktop\8005-workspace\repos\8005-agv-control-server\scripts\field\FieldOperator.psm1' -Force
$field = New-FieldOperator -VehicleHost agv01 -ServerHost factory01 -AgvId '老厂前线新多仓位1'
$journeyId = 'afc0ce0a-96f3-1558-80f3-0b247535c994'
function Say([string]$m) { Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $m" }
$handled = @{}
while ($true) {
    $journey = Get-FieldJourney -Field $field -JourneyId $journeyId
    $stage = [string]$journey.Stage
    $sequence = [int]$journey.CurrentStopSequence
    if ($stage -eq 'Completed') { Say "journey $journeyId Completed"; break }
    if ($stage -eq 'Blocked') { throw "journey $journeyId Blocked at stop ${sequence}: $($journey.BlockReasonCode)" }
    if ($stage -eq 'AwaitingSublot' -and -not $handled.ContainsKey($sequence)) {
        $handled[$sequence] = $true
        Say "stop ${sequence}: load"
        $load = Invoke-FieldActLoad -Field $field -JourneyId $journeyId -Sequence $sequence
        Say "stop ${sequence}: load $($load.Status)"
        continue
    }
    if ($stage -eq 'AwaitingUnloadResult') {
        Say 'gate: unload'
        $unload = Invoke-FieldActUnload -Field $field -JourneyId $journeyId
        Say "gate: unload $((@($unload.Operations.Values)) -join ',')"
        continue
    }
    Start-Sleep -Seconds 5
}
