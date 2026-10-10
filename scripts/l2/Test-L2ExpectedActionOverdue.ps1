#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2ExpectedActionOverdue.psm1: the one setup key that shortens REQ-0358's expected-action-overdue
    threshold on both ends of a real-onboard run (control-server#167).

.DESCRIPTION
    Pure input, no rig and no server, a few seconds. What the two ends do with the threshold is covered by their own
    suites (ExpectedActionOverdueOptions on the server, WorkflowSettings on the onboard) and by the real-onboard
    scenario real-onboard-expected-action-overdue; this covers only the orchestrator's half: that a bad value fails
    before anything starts, that a scenario without the key leaves both ends at their shipped defaults, and that one
    value lands on both ends in the form each of them parses.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2ExpectedActionOverdue.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2ExpectedActionOverdue.psm1') -Force

$wrong = 0
function Test-Case([string]$Name, [scriptblock]$Body, [string]$Throws) {
    $failure = $null
    try { $null = & $Body } catch { $failure = $_.Exception.Message }
    $asExpected = if ($Throws) { $null -ne $failure -and $failure.Contains($Throws) } else { $null -eq $failure }
    if (-not $asExpected) { $script:wrong++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($asExpected) { 'ok  ' } else { 'BAD ' }), $Name,
        $(if ($failure) { "threw: $failure" } else { 'no error' }))
}

Test-Case 'no key leaves both ends at their shipped defaults' {
    if ($null -ne (Resolve-L2ExpectedActionOverdueThreshold -Setup @{ Onboard = 'Real' } -Where 't' -RealOnboard $true)) {
        throw 'expected $null'
    }
}
Test-Case 'a time span on the real rig is that threshold' {
    $threshold = Resolve-L2ExpectedActionOverdueThreshold -Setup @{ Onboard = 'Real'; ExpectedActionOverdueThreshold = '00:00:20' } `
        -Where 't' -RealOnboard $true
    if ($threshold -ne [TimeSpan]::FromSeconds(20)) { throw "expected 00:00:20, got '$threshold'" }
}
Test-Case 'the synthetic rig is refused' {
    Resolve-L2ExpectedActionOverdueThreshold -Setup @{ ExpectedActionOverdueThreshold = '00:00:20' } -Where 't' -RealOnboard $false
} "needs Onboard = 'Real'"
Test-Case 'zero is refused' {
    Resolve-L2ExpectedActionOverdueThreshold -Setup @{ ExpectedActionOverdueThreshold = '00:00:00' } -Where 't' -RealOnboard $true
} 'must be positive'
Test-Case 'a malformed value is refused' {
    Resolve-L2ExpectedActionOverdueThreshold -Setup @{ ExpectedActionOverdueThreshold = '20 seconds' } -Where 't' -RealOnboard $true
} 'not a time span'
Test-Case 'a bare number is refused rather than read as days or milliseconds' {
    Resolve-L2ExpectedActionOverdueThreshold -Setup @{ ExpectedActionOverdueThreshold = 20 } -Where 't' -RealOnboard $true
} 'not a time span'
Test-Case 'a value the onboard cannot hold in whole milliseconds is refused' {
    Resolve-L2ExpectedActionOverdueThreshold -Setup @{ ExpectedActionOverdueThreshold = '00:00:20.0005' } -Where 't' -RealOnboard $true
} 'whole milliseconds'
Test-Case 'the server gets the threshold as a time span it parses back to the same value' {
    $environment = @{}
    Set-L2ExpectedActionOverdueServerSetting -Environment $environment -Threshold ([TimeSpan]::FromSeconds(20))
    $text = $environment['ExpectedActionOverdue__threshold']
    if ($text -ne '00:00:20') { throw "expected '00:00:20', got '$text'" }
    if ([TimeSpan]::Parse($text, [Globalization.CultureInfo]::InvariantCulture) -ne [TimeSpan]::FromSeconds(20)) { throw 'does not parse back' }
}
Test-Case 'without a threshold the server environment gets no key, so its shipped file decides' {
    $environment = @{ Other = 'x' }
    Set-L2ExpectedActionOverdueServerSetting -Environment $environment -Threshold $null
    if ($environment.ContainsKey('ExpectedActionOverdue__threshold') -or $environment.Count -ne 1) { throw "keys: $($environment.Keys -join ', ')" }
}

# The shipped onboard appsettings.json has a workflow section without the key, and New-L2PeerStage hands the Configure
# block a PSCustomObject read from it, which refuses assignment to a property it does not have.
function New-ShippedOnboardSettings {
    return ('{ "agvId": "x", "workflow": { "operationTimeoutMs": 120000, "maxReopenAttempts": 2 } }' | ConvertFrom-Json)
}
Test-Case 'the onboard stage copy gets workflow.expectedActionOverdueMs in milliseconds' {
    $settings = New-ShippedOnboardSettings
    Set-L2ExpectedActionOverdueOnboardSetting -Settings $settings -Threshold ([TimeSpan]::FromSeconds(20))
    $written = $settings | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    if ($written.workflow.expectedActionOverdueMs -isnot [long] -or $written.workflow.expectedActionOverdueMs -ne 20000) {
        throw "expected 20000, got '$($written.workflow.expectedActionOverdueMs)'"
    }
    if ($written.workflow.operationTimeoutMs -ne 120000) { throw 'operationTimeoutMs was touched' }
}
Test-Case 'without a threshold the onboard stage copy has no expectedActionOverdueMs, so 3 x operationTimeoutMs applies' {
    $settings = New-ShippedOnboardSettings
    Set-L2ExpectedActionOverdueOnboardSetting -Settings $settings -Threshold $null
    $json = $settings | ConvertTo-Json -Depth 8
    if ($json.Contains('expectedActionOverdueMs')) { throw $json }
}

# control-server#541: the bound on the overdue alarm a scenario waits for. Snapshots shaped as Get-L2RealInbound returns them
# (Payload from ConvertFrom-Json -DateKind String, so raisedAt is text).
function New-AlarmSnapshot([string]$MessageId, [object[]]$Alarms) {
    return [pscustomobject]@{ MessageId = $MessageId; Payload = [pscustomobject]@{ alarms = $Alarms } }
}
function New-Alarm([string]$Code, [string]$Slot, [string]$RaisedAt) {
    return [pscustomobject]@{ alarmId = [guid]::NewGuid().ToString(); code = $Code; subjectType = 'SLOT'; subjectId = $Slot; raisedAt = $RaisedAt }
}
$overdue = 'SLOT_EXPECTED_ACTION_OVERDUE'
$unloadStarted = [DateTimeOffset]::Parse('2026-10-10T08:00:30+00:00')
$loadSnapshot = New-AlarmSnapshot 'load' @(New-Alarm $overdue '3' '2026-10-10T08:00:05.123Z')
$loadStillListed = New-AlarmSnapshot 'later-but-old-alarm' @(New-Alarm $overdue '3' '2026-10-10T08:00:05.123Z')
$unloadSnapshot = New-AlarmSnapshot 'unload' @(New-Alarm $overdue '3' '2026-10-10T08:00:52.400Z')

Test-Case 'the defect: the load snapshot alone met the old probe (code and slot only) but does not meet the bounded one' {
    $old = @(@($loadSnapshot) | Where-Object { @(@($_.Payload.alarms) | Where-Object {
                    [string]$_.code -eq $overdue -and [string]$_.subjectId -eq '3' }).Count -gt 0 })
    if ($old.Count -ne 1) { throw 'the old predicate was expected to match the load snapshot' }
    $bounded = Select-L2OverdueAlarmSnapshot -Snapshot @($loadSnapshot) -Code $overdue -SlotNo '3' -RaisedAfter $unloadStarted
    if ($null -ne $bounded) { throw "expected `$null, got $($bounded.MessageId)" }
}
Test-Case 'a later snapshot still listing the load alarm does not count: raisedAt is bounded, not arrival' {
    $found = Select-L2OverdueAlarmSnapshot -Snapshot @($loadSnapshot, $loadStillListed) -Code $overdue -SlotNo '3' -RaisedAfter $unloadStarted
    if ($null -ne $found) { throw "expected `$null, got $($found.MessageId)" }
}
Test-Case 'the unload alarm raised after the bound is found, in the newest matching snapshot' {
    $found = Select-L2OverdueAlarmSnapshot -Snapshot @($loadSnapshot, $loadStillListed, $unloadSnapshot) -Code $overdue -SlotNo '3' -RaisedAfter $unloadStarted
    if ($null -eq $found -or $found.MessageId -ne 'unload') { throw "expected unload, got $($found.MessageId)" }
}
Test-Case 'an alarm raised exactly at the bound counts' {
    $at = New-AlarmSnapshot 'at' @(New-Alarm $overdue '3' '2026-10-10T08:00:30Z')
    if ($null -eq (Select-L2OverdueAlarmSnapshot -Snapshot @($at) -Code $overdue -SlotNo '3' -RaisedAfter $unloadStarted)) { throw 'expected a match' }
}
Test-Case 'another slot or another code does not count' {
    $other = New-AlarmSnapshot 'other' @((New-Alarm $overdue '4' '2026-10-10T08:00:52Z'), (New-Alarm 'SLOT_DOOR_OPEN' '3' '2026-10-10T08:00:52Z'))
    if ($null -ne (Select-L2OverdueAlarmSnapshot -Snapshot @($other) -Code $overdue -SlotNo '3' -RaisedAfter $unloadStarted)) { throw 'expected $null' }
}
Test-Case 'an alarm whose raisedAt is missing or does not parse does not count' {
    $broken = New-AlarmSnapshot 'broken' @((New-Alarm $overdue '3' 'not a time'),
        [pscustomobject]@{ alarmId = 'x'; code = $overdue; subjectType = 'SLOT'; subjectId = '3' })
    if ($null -ne (Select-L2OverdueAlarmSnapshot -Snapshot @($broken) -Code $overdue -SlotNo '3' -RaisedAfter $unloadStarted)) { throw 'expected $null' }
}
Test-Case 'no snapshots at all is $null, not an error' {
    if ($null -ne (Select-L2OverdueAlarmSnapshot -Snapshot @() -Code $overdue -SlotNo '3' -RaisedAfter $unloadStarted)) { throw 'expected $null' }
}

if ($wrong -gt 0) {
    Write-Host "$wrong case(s) came out wrong."
    exit 1
}
Write-Host 'All cases as expected.'
