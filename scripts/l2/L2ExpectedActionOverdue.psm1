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
    throw 'Not implemented (control-server#167 test commit).'
}

# The server half: ExpectedActionOverdue__threshold, which ExpectedActionOverdueOptions reads over its shipped file.
function Set-L2ExpectedActionOverdueServerSetting {
    param(
        [Parameter(Mandatory)][hashtable]$Environment,
        [AllowNull()][object]$Threshold
    )
    throw 'Not implemented (control-server#167 test commit).'
}

# The onboard half, on the object New-L2PeerStage read from the stage copy. The shipped file has no such property, and a
# PSCustomObject refuses assignment to one it lacks, hence Add-Member.
function Set-L2ExpectedActionOverdueOnboardSetting {
    param(
        [Parameter(Mandatory)][object]$Settings,
        [AllowNull()][object]$Threshold
    )
    throw 'Not implemented (control-server#167 test commit).'
}

Export-ModuleMember -Function Resolve-L2ExpectedActionOverdueThreshold, Set-L2ExpectedActionOverdueServerSetting,
    Set-L2ExpectedActionOverdueOnboardSetting
