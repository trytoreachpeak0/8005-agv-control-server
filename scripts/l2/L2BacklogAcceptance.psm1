#Requires -Version 7

<#
Read-L2BacklogAcceptance: "the dispatch round accepted this demand", judged on evidence later rounds do not rewrite.

Added for control-server#730 in its own file, the way L2SingleRow.psm1 was. A scenario that wants it imports it next
to L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2BacklogAcceptance.psm1') -Force

Why it exists. Scenarios used to read JourneyBacklog.ReasonCode once and require 'ACCEPTED'. Acceptance does write that
code (WireToGateStore, the same SaveChanges that writes AcceptedAt = snapshot.AcceptedAt and LastSeenAt = AcceptedAt),
but every later dispatch round still judges the demand and DispatchRoundRunner.UpsertBacklog overwrites ReasonCode
with DEMAND_ALREADY_ACCEPTED and moves LastSeenAt to that round. So which value a single read sees depends on whether
the next round has finished yet: control-server#724's only CI round went red on normal-load's L2-NL-02 this way
(l2 run 38721626350). Waiting for 'ACCEPTED' instead is the same race, since what is waited for is the value that
gets overwritten.

What later rounds leave alone is AcceptedAt (a release, DemandReleaseService, clears it, and the redispatch that
follows rewrites it -- see KNOWN LIMITS). And the per-vehicle
verdicts of control-server#744 are no help: the dashboard gives them for unaccepted rows only, from a board each round
replaces whole.

THE CRITERION. All of these, from one read of each table:

    1. exactly one JourneyBacklog row and exactly one AcceptedDemands row for the demand;
    2. the backlog row's AcceptedAt is set and is the same instant as AcceptedDemands.AcceptedAt -- it was accepted,
       and by this acceptance;
    3. ReasonCode is ACCEPTED, or it is DEMAND_ALREADY_ACCEPTED and LastSeenAt is later than AcceptedAt -- the code was
       written by a round after the acceptance, not by the acceptance itself. Any other code is red.

KNOWN LIMITS. The criterion is for a demand accepted once, by the only vehicle that bid for it in that round. Every
scenario that uses it today is a single vehicle with no release (control-server#730 review checked the five).

  - The code acceptance wrote is not kept anywhere once a later round overwrites it. A product that accepted the
    demand but wrote some other code at that moment, then had it overwritten with DEMAND_ALREADY_ACCEPTED, passes.
  - Several vehicles bidding in the acceptance round: DispatchRoundRunner writes the losers' verdict
    DEMAND_ALREADY_ACCEPTED with that round's start time (`now`), which is earlier than AcceptedAt (taken when the
    catalog is re-read during acceptance). Until the next round moves LastSeenAt, rule 3 is red. A multi-vehicle
    scenario would have to wait for that round first.
  - A release and redispatch: the redispatch reuses the first acceptance's AcceptedDemands row (WireToGateStore,
    `if (!redispatch)`) but writes the backlog row's AcceptedAt again with the redispatch time, so rule 2 is red for
    good. Not usable after a release.

What goes red: not accepted, AcceptedAt missing or not this acceptance's, a code other than those two, and
DEMAND_ALREADY_ACCEPTED without a later round to have written it.

The pure half, Test-L2BacklogAcceptance, is what Test-L2BacklogAcceptance.ps1 pins, offline.
#>

Set-StrictMode -Version Latest

$script:Expected = 'ACCEPTED (or DEMAND_ALREADY_ACCEPTED written after AcceptedAt) / AcceptedAt set and = AcceptedDemands.AcceptedAt'

function ConvertTo-L2Instant([object]$value) {
    if ($null -eq $value) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse([string]$value, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) {
        return $parsed
    }
    return $null
}

function Test-L2BacklogAcceptance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$BacklogRows,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$AcceptedRows
    )

    if ($BacklogRows.Count -ne 1 -or $AcceptedRows.Count -ne 1) {
        return [pscustomobject]@{
            Passed   = $false
            Expected = $script:Expected
            Actual   = "($($BacklogRows.Count) backlog rows, $($AcceptedRows.Count) accepted rows, expected 1 and 1)"
        }
    }

    $backlog = $BacklogRows[0]
    $reason = [string]$backlog.ReasonCode
    $acceptedAt = ConvertTo-L2Instant $backlog.AcceptedAt
    $lastSeenAt = ConvertTo-L2Instant $backlog.LastSeenAt
    $demandAcceptedAt = ConvertTo-L2Instant $AcceptedRows[0].AcceptedAt

    $acceptedByThisAcceptance = $null -ne $acceptedAt -and $null -ne $demandAcceptedAt -and $acceptedAt -eq $demandAcceptedAt
    $codeHolds = $reason -eq 'ACCEPTED' -or
        ($reason -eq 'DEMAND_ALREADY_ACCEPTED' -and $null -ne $acceptedAt -and $null -ne $lastSeenAt -and $lastSeenAt -gt $acceptedAt)

    return [pscustomobject]@{
        Passed   = [bool]($acceptedByThisAcceptance -and $codeHolds)
        Expected = $script:Expected
        Actual   = "ReasonCode=$reason / AcceptedAt=$($backlog.AcceptedAt ?? '(null)') / LastSeenAt=$($backlog.LastSeenAt ?? '(null)')" +
                   " / AcceptedDemands.AcceptedAt=$($AcceptedRows[0].AcceptedAt ?? '(null)')"
    }
}

function Read-L2BacklogAcceptance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId
    )

    $backlog = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT ReasonCode, AcceptedAt, LastSeenAt FROM JourneyBacklog WHERE DemandId = '$DemandId'"
    $accepted = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT AcceptedAt FROM AcceptedDemands WHERE DemandId = '$DemandId'"
    return Test-L2BacklogAcceptance -BacklogRows $backlog -AcceptedRows $accepted
}

Export-ModuleMember -Function Test-L2BacklogAcceptance, Read-L2BacklogAcceptance
