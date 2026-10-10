#Requires -Version 7

<#
.SYNOPSIS
    The setup key that shortens REQ-0358's expected-action-overdue threshold on both ends of a real-onboard run
    (control-server#167).

.DESCRIPTION
    The onboard raises SLOT_EXPECTED_ACTION_OVERDUE once the slot it waits on has waited, counted from the operation's
    first unlock, as long as `workflow.expectedActionOverdueMs` (shipped: absent, meaning 3 x operationTimeoutMs, six
    minutes). The server cannot read that value; it keeps its own copy, `ExpectedActionOverdue:threshold` (shipped:
    00:06:00), to turn the alarm's raisedAt into the dashboard's waitedSeconds. Configured apart, the two would differ
    by exactly the difference, and nothing in an L2 run would notice. So one setup key sets both:

        ExpectedActionOverdueThreshold = '00:00:20'

    written into the onboard's stage copy of appsettings.json in milliseconds, and into the server's environment as a
    time span. A scenario without the key leaves both ends at their shipped defaults, and neither file is touched.

    This is not what README item 11 forbids. That item is about operationTimeoutMs, a safety-relevant executor timing;
    this key leaves it alone. The expected-action-overdue threshold is a site-calibrated parameter (REQ-0358 notes 2
    and 3) that decides only when a wait is reported, and changes no executor timing.

    The key is checked before any process starts: it needs the real onboard (the synthetic peer never raises the
    alarm, so a threshold there would leave a scenario green about something else), a time span string, positive, and
    whole milliseconds that fit the onboard's int.
#>

Set-StrictMode -Version Latest

# $null when the setup file does not name the key; otherwise the threshold as a TimeSpan. Throws on anything else.
function Resolve-L2ExpectedActionOverdueThreshold {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where,
        [bool]$RealOnboard
    )
    if (-not $Setup.ContainsKey('ExpectedActionOverdueThreshold')) { return $null }
    $value = $Setup.ExpectedActionOverdueThreshold
    if (-not $RealOnboard) {
        throw "ExpectedActionOverdueThreshold in $Where needs Onboard = 'Real': the synthetic peer never raises SLOT_EXPECTED_ACTION_OVERDUE, so a threshold would leave the scenario green about something else."
    }
    $threshold = [TimeSpan]::Zero
    # A bare number is refused rather than guessed at: TimeSpan reads '20' as twenty days, the onboard key is milliseconds.
    if ($value -isnot [string] -or
        -not [TimeSpan]::TryParse($value, [Globalization.CultureInfo]::InvariantCulture, [ref]$threshold)) {
        throw "ExpectedActionOverdueThreshold in $Where is not a time span such as '00:00:20': '$value'."
    }
    if ($threshold -le [TimeSpan]::Zero) {
        throw "ExpectedActionOverdueThreshold in $Where must be positive, not '$value'."
    }
    if ($threshold.Ticks % [TimeSpan]::TicksPerMillisecond -ne 0 -or $threshold.TotalMilliseconds -gt [int]::MaxValue) {
        throw "ExpectedActionOverdueThreshold in $Where must be whole milliseconds up to $([int]::MaxValue) ms, the onboard's workflow.expectedActionOverdueMs is an int: '$value'."
    }
    return $threshold
}

# The server half: ExpectedActionOverdue__threshold, which ExpectedActionOverdueOptions reads over its shipped file.
function Set-L2ExpectedActionOverdueServerSetting {
    param(
        [Parameter(Mandatory)][hashtable]$Environment,
        [AllowNull()][object]$Threshold
    )
    if ($null -eq $Threshold) { return }
    $Environment['ExpectedActionOverdue__threshold'] = ([TimeSpan]$Threshold).ToString('c', [Globalization.CultureInfo]::InvariantCulture)
}

# The onboard half, on the object New-L2PeerStage read from the stage copy. The shipped file has no such property, and a
# PSCustomObject refuses assignment to one it lacks, hence Add-Member.
function Set-L2ExpectedActionOverdueOnboardSetting {
    param(
        [Parameter(Mandatory)][object]$Settings,
        [AllowNull()][object]$Threshold
    )
    if ($null -eq $Threshold) { return }
    $Settings.workflow | Add-Member -NotePropertyName 'expectedActionOverdueMs' `
        -NotePropertyValue ([int]([TimeSpan]$Threshold).TotalMilliseconds) -Force
}

<#
The newest OnboardAlarmSnapshot that reports $Code for slot $SlotNo in an alarm raised at or after $RaisedAfter, or $null
(control-server#541). The server's inbox keeps every snapshot of the run, so a scenario waiting for a later operation's
overdue alarm on a slot an earlier operation already reported is otherwise satisfied at once by the earlier snapshot:
g3-slot-fault-declaration waited one second at the unload, declared, and was rightly answered 409
SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE. A later snapshot that still carries the earlier alarm does not count either: it
is the alarm's raisedAt that is bounded, not when the snapshot arrived. An alarm entry without a raisedAt that parses is
not counted. raisedAt is the onboard's clock; on the real-onboard rig that is the clock of this machine.
#>
function Select-L2OverdueAlarmSnapshot {
    param(
        [object[]]$Snapshot,
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string]$SlotNo,
        [Parameter(Mandatory)][DateTimeOffset]$RaisedAfter
    )
    $matching = @($Snapshot | Where-Object {
            $null -ne $_ -and $null -ne $_.PSObject.Properties['Payload'] -and $null -ne $_.Payload -and
            $null -ne $_.Payload.PSObject.Properties['alarms'] -and
            @(@($_.Payload.alarms) | Where-Object {
                    $raisedAt = [DateTimeOffset]::MinValue
                    $null -ne $_ -and
                    $null -ne $_.PSObject.Properties['code'] -and [string]$_.code -eq $Code -and
                    $null -ne $_.PSObject.Properties['subjectId'] -and [string]$_.subjectId -eq $SlotNo -and
                    $null -ne $_.PSObject.Properties['raisedAt'] -and
                    [DateTimeOffset]::TryParse([string]$_.raisedAt, [Globalization.CultureInfo]::InvariantCulture,
                        [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$raisedAt) -and
                    $raisedAt -ge $RaisedAfter
                }).Count -gt 0
        })
    if ($matching.Count -eq 0) { return $null }
    return $matching[-1]
}

Export-ModuleMember -Function Resolve-L2ExpectedActionOverdueThreshold, Set-L2ExpectedActionOverdueServerSetting,
    Set-L2ExpectedActionOverdueOnboardSetting, Select-L2OverdueAlarmSnapshot
