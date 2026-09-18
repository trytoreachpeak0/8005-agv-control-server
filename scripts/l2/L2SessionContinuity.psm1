#Requires -Version 7

<#
Whether a session came through a rejected pre-departure check intact, when the reading is taken after the
vehicle departed (control-server#138).

A file of its own for the reason L2Change.psm1 gives, and pure: the scenario reads the database, RIoT and the
inbox, and hands the facts in. Test-L2SessionContinuity.ps1 checks it on constructed input, the red run of
control-server#128 among them.

Why a departed vehicle's session is allowed to read other than Ready: departing is the one thing that makes
this session stop being Ready by design. Once the server has created the gate order, the vehicle-safety
projection it serves the onboard (HttpRiotMovementGateway.ReadVehicleSafetyAsync) reports the vehicle as
UNKNOWN (RIOT_NONFINAL_ORDER_PRESENT) or MOVING, the onboard reports departureSafe=false with
VEHICLE_NOT_READY or ACTION_NOT_ALLOWED_IN_STATE, and the server holds the session at
RecoveryRequired / DEPARTURE_SAFETY_NOT_READY until the vehicle stands still again. docs/RELEASE-CANDIDATE.md
section 8 names that the expected behaviour of the safety gate, and evidence/g3/20260830-issue14-field-closed-loop
saw it on the real vehicle on both legs. The onboard polls the projection about once a second, so a reading
taken just after departure lands on either side of that report; that is the whole of the old flake.
#>

Set-StrictMode -Version Latest

# The only safety reasons a departure itself produces: the vehicle-motion ones. Every slot reason, and
# SLOT_STATE_UNKNOWN in particular, is something the departure does not explain.
$script:DepartureReasons = @('VEHICLE_NOT_READY', 'ACTION_NOT_ALLOWED_IN_STATE')
# RIoT order states that are not final, the list HttpRiotMovementGateway.NonFinalOrderStates reads.
$script:NonFinalOrderStates = @(1, 3, 7, 9)

<#
The G3-03-06 verdict: rejecting the expired check did not break the session.

Passes on one of two paths, named in the result's Path so the evidence says which one it took:

- Ready: the session is Ready at the reading.
- DepartureExplainedDemotion: the session is not Ready, and every one of these holds -- its reason is
  DEPARTURE_SAFETY_NOT_READY; it has safety reason codes; none of them is SLOT_STATE_UNKNOWN; all of them are
  vehicle-motion reasons; the safety revision that carries them was received after the gate order was created;
  and the gate order is still not final in RIoT. Missing any one, the demotion is not the departure's and fails.

Either way two things are required first, and they are what the rejection could actually have broken: the
session generation did not change, and after the rejection the vehicle departed while its last reported safety
state was safe. The journey only advances on a Ready session (JourneyRuntimeEngine.CurrentReadySessionAsync),
so a gate order created after the rejection is the server itself saying the session was still usable.

SafetyChanges are the received SafetyStateChanged messages, each with At, Version and DepartureSafe.
#>
function Test-L2SessionKeptThroughDeparture {
    param(
        [Parameter(Mandatory)][long]$GenerationBefore,
        [Parameter(Mandatory)][object]$Session,
        [AllowNull()][Nullable[DateTimeOffset]]$ProblemAt,
        [AllowNull()][Nullable[DateTimeOffset]]$GateIntentCreatedAt,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$SafetyChanges,
        [AllowNull()][Nullable[int]]$GateOrderState
    )

    function Fail([string]$reason) { return [pscustomobject]@{ Passed = $false; Path = $null; Reason = $reason } }
    function Pass([string]$path) { return [pscustomobject]@{ Passed = $true; Path = $path; Reason = $null } }

    if ([long]$Session.SessionGeneration -ne $GenerationBefore) {
        return Fail "session generation changed: $GenerationBefore -> $($Session.SessionGeneration)"
    }
    if ($null -eq $ProblemAt -or $null -eq $GateIntentCreatedAt -or $GateIntentCreatedAt -le $ProblemAt) {
        return Fail 'no departure after the rejection: the gate order must be created after the ProtocolProblem'
    }
    $beforeDeparture = @($SafetyChanges | Where-Object { $_.At -lt $GateIntentCreatedAt } | Sort-Object At)
    if ($beforeDeparture.Count -eq 0 -or -not [bool]$beforeDeparture[-1].DepartureSafe) {
        return Fail 'the last safety state before the departure was not safe'
    }

    if ([string]$Session.Readiness -eq 'Ready') { return Pass 'Ready' }

    if ([string]$Session.ReasonCode -ne 'DEPARTURE_SAFETY_NOT_READY') {
        return Fail "not Ready, and the reason code $($Session.ReasonCode) is not DEPARTURE_SAFETY_NOT_READY"
    }
    $reasons = @(if ([string]$Session.SafetyReasonCodesJson) { [string]$Session.SafetyReasonCodesJson | ConvertFrom-Json })
    if ($reasons.Count -eq 0) {
        return Fail 'not Ready with no safety reason recorded'
    }
    if ($reasons -contains 'SLOT_STATE_UNKNOWN') {
        return Fail 'not Ready with SLOT_STATE_UNKNOWN: the slot facts are unknown, which no departure explains'
    }
    $foreign = @($reasons | Where-Object { $_ -cnotin $script:DepartureReasons })
    if ($foreign.Count -gt 0) {
        return Fail "not Ready with $($foreign -join ','), not a vehicle-motion reason"
    }
    $demoting = @($SafetyChanges | Where-Object { [long]$_.Version -eq [long]$Session.SafetyRevision })
    if ($demoting.Count -eq 0 -or $demoting[0].At -le $GateIntentCreatedAt) {
        return Fail "safety revision $($Session.SafetyRevision) was not received after the departure"
    }
    if ($null -eq $GateOrderState -or [int]$GateOrderState -notin $script:NonFinalOrderStates) {
        return Fail "the gate order is not in flight (state $(if ($null -eq $GateOrderState) { 'none' } else { $GateOrderState }))"
    }
    return Pass 'DepartureExplainedDemotion'
}

Export-ModuleMember -Function Test-L2SessionKeptThroughDeparture
