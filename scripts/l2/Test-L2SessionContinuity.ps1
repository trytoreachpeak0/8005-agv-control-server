#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2SessionContinuity.psm1: the G3-03-06 verdict that rejecting an expired pre-departure
    check did not break the session (control-server#138).

.DESCRIPTION
    Pure input, no rig and no database, a second.

    The first case is the red run itself -- journey-selfcheck-001 of control-server#128, runId
    20260918T105056911Z, control server 10f43ab4, onboard 8153946b. Its session ended RecoveryRequired /
    DEPARTURE_SAFETY_NOT_READY because the vehicle departed: the gate order was created at 10:52:06.418, the
    onboard's next vehicle-safety poll saw that non-final order, and safety revision 10 (VEHICLE_NOT_READY,
    unknown present) landed at 10:52:06.598, before the scenario read the session. The criterion as it stood
    read that as a broken session. It must pass, on the "departure explained the demotion" path.

    Every other case must fail, each for its own reason: those are the things the rejection could have done to
    the session, and the verdict must still catch every one of them.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2SessionContinuity.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2SessionContinuity.psm1') -Force

function At([string]$time) { return [DateTimeOffset]::Parse("2026-09-18T$($time)+00:00", [Globalization.CultureInfo]::InvariantCulture) }

function Session {
    param([long]$Generation = 1, [string]$Readiness = 'RecoveryRequired', [string]$ReasonCode = 'DEPARTURE_SAFETY_NOT_READY',
        [string]$Reasons = '["VEHICLE_NOT_READY"]', [long]$Revision = 10)
    return [pscustomobject]@{
        SessionGeneration = $Generation; Readiness = $Readiness; ReasonCode = $ReasonCode
        SafetyReasonCodesJson = $Reasons; SafetyRevision = $Revision
    }
}

# The red run's safety revisions as the server received them. v8 is the bystander slot's lock forced open, v9
# its release -- the change that expired the check -- and v10 the report after the vehicle was given its
# gate order. The ProtocolProblem time is the onboard's own send time from its log (18:52:06.3397 local).
$redChanges = @(
    [pscustomobject]@{ At = (At '10:52:05.958'); Version = 8; DepartureSafe = $false }
    [pscustomobject]@{ At = (At '10:52:06.296'); Version = 9; DepartureSafe = $true }
    [pscustomobject]@{ At = (At '10:52:06.598'); Version = 10; DepartureSafe = $false }
)
$red = @{
    GenerationBefore = 1; Session = (Session); ProblemAt = (At '10:52:06.340'); GateIntentCreatedAt = (At '10:52:06.418')
    SafetyChanges = $redChanges; GateOrderState = 1
}

function With([hashtable]$base, [hashtable]$changes) {
    $copy = $base.Clone()
    foreach ($key in $changes.Keys) { $copy[$key] = $changes[$key] }
    return $copy
}

$cases = @(
    @{ Name = 'the red run: demoted by its own departure (control-server#128 journey-selfcheck-001)'
       Input = $red; Pass = $true; Path = 'DepartureExplainedDemotion' }
    @{ Name = 'the green runs: read before the vehicle-safety poll landed'
       Input = (With $red @{ Session = (Session -Readiness 'Ready' -ReasonCode 'READY' -Reasons '[]' -Revision 9) })
       Pass = $true; Path = 'Ready' }
    @{ Name = 'moving rather than holding the order: ACTION_NOT_ALLOWED_IN_STATE'
       Input = (With $red @{ Session = (Session -Reasons '["ACTION_NOT_ALLOWED_IN_STATE"]'); GateOrderState = 3 })
       Pass = $true; Path = 'DepartureExplainedDemotion' }

    @{ Name = 'the session was re-established'
       Input = (With $red @{ Session = (Session -Generation 2 -Readiness 'Ready' -ReasonCode 'READY' -Reasons '[]') })
       Pass = $false; Reason = 'generation' }
    @{ Name = 'no departure at all'
       Input = (With $red @{ GateIntentCreatedAt = $null }); Pass = $false; Reason = 'departure' }
    @{ Name = 'the vehicle never rejected the check'
       Input = (With $red @{ ProblemAt = $null }); Pass = $false; Reason = 'departure' }
    @{ Name = 'departed before the rejection'
       Input = (With $red @{ ProblemAt = (At '10:52:06.500') }); Pass = $false; Reason = 'departure' }
    @{ Name = 'the last safety state before departing was unsafe'
       Input = (With $red @{ SafetyChanges = @($redChanges[0], $redChanges[2]) }); Pass = $false; Reason = 'last safety state' }
    @{ Name = 'Ready is not the answer and the reason is another one'
       Input = (With $red @{ Session = (Session -ReasonCode 'HANDSHAKE_INCOMPLETE') }); Pass = $false; Reason = 'reason code' }
    @{ Name = 'no safety reason codes recorded'
       Input = (With $red @{ Session = (Session -Reasons '[]') }); Pass = $false; Reason = 'no safety reason' }
    @{ Name = 'the slot facts are unknown too'
       Input = (With $red @{ Session = (Session -Reasons '["SLOT_STATE_UNKNOWN","VEHICLE_NOT_READY"]') }); Pass = $false; Reason = 'SLOT_STATE_UNKNOWN' }
    @{ Name = 'a slot is open as well'
       Input = (With $red @{ Session = (Session -Reasons '["LOCK_NOT_CLOSED","VEHICLE_NOT_READY"]') }); Pass = $false; Reason = 'not a vehicle-motion reason' }
    @{ Name = 'an unsafe revision landed between the safe one and the departure'
       Input = (With $red @{ SafetyChanges = @($redChanges[0], $redChanges[1],
            [pscustomobject]@{ At = (At '10:52:06.400'); Version = 10; DepartureSafe = $false }) })
       Pass = $false; Reason = 'last safety state' }
    @{ Name = 'the session is held on a revision from before the departure'
       Input = (With $red @{ Session = (Session -Revision 8) }); Pass = $false; Reason = 'not received after the departure' }
    @{ Name = 'the demoting revision is not among the received changes'
       Input = (With $red @{ Session = (Session -Revision 11) }); Pass = $false; Reason = 'not received after the departure' }
    @{ Name = 'the gate order has finished and the session is still not Ready'
       Input = (With $red @{ GateOrderState = 5 }); Pass = $false; Reason = 'gate order' }
    @{ Name = 'no gate order in RIoT'
       Input = (With $red @{ GateOrderState = $null }); Pass = $false; Reason = 'gate order' }
)

$wrong = 0
foreach ($case in $cases) {
    $arguments = $case.Input
    $result = Test-L2SessionKeptThroughDeparture @arguments
    $asExpected = ($result.Passed -eq $case.Pass) -and
        $(if ($case.Pass) { $result.Path -ceq $case.Path } else { ([string]$result.Reason).Contains($case.Reason) })
    if (-not $asExpected) { $wrong++ }
    $verdict = if ($result.Passed) { "PASS via $($result.Path)" } else { "FAIL ($($result.Reason))" }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($asExpected) { 'ok  ' } else { 'BAD ' }), $case.Name, $verdict)
}

$passing = @($cases | Where-Object { $_.Pass }).Count
if ($wrong -gt 0) {
    Write-Host "L2SessionContinuity self-check: $wrong of $($cases.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2SessionContinuity self-check: all $($cases.Count) cases as expected ($passing pass, $($cases.Count - $passing) fail)."
