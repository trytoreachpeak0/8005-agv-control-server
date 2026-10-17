#Requires -Version 7
<#
.SYNOPSIS
    Self-check of L2BacklogAcceptance.psm1: the acceptance criterion holds after later rounds rewrote the code, and
    goes red on each thing it is there to catch.

.DESCRIPTION
    Under a second, no rig and no SQLite: Test-L2BacklogAcceptance is pure, so the rows are given as they come back
    from Invoke-L2Query (TEXT columns, as Microsoft.Data.Sqlite returns the DateTimeOffset columns EF writes).

    The first green case is the row control-server#724's only CI round left behind (l2 run 38721626350, normal-load,
    snapshots/db-JourneyBacklog.json and db-AcceptedDemands.json), on which the single read of ReasonCode went red.
    A green run on the rig almost never reaches the red cases, so they are pinned here.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2BacklogAcceptance.psm1') -Force

$acceptedAt = '2026-10-15 10:53:18.0348616+00:00'
$laterRound = '2026-10-15 10:53:55.7551632+00:00'

function Backlog([string]$reason, [object]$at, [object]$lastSeen) {
    [pscustomobject]@{ ReasonCode = $reason; AcceptedAt = $at; LastSeenAt = $lastSeen }
}
function Accepted([object]$at) { [pscustomobject]@{ AcceptedAt = $at } }

$cases = @(
    @{ Case = 'CI 38721626350 row: rewritten to DEMAND_ALREADY_ACCEPTED by a later round'; Pass = $true
       Backlog = @(Backlog 'DEMAND_ALREADY_ACCEPTED' $acceptedAt $laterRound); Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'read before the next round: ACCEPTED as acceptance wrote it'; Pass = $true
       Backlog = @(Backlog 'ACCEPTED' $acceptedAt $acceptedAt); Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'same instant in another offset is the same acceptance'; Pass = $true
       Backlog = @(Backlog 'ACCEPTED' $acceptedAt $acceptedAt); Accepted = @(Accepted '2026-10-15 18:53:18.0348616+08:00') }

    @{ Case = 'not accepted: refused with another code, no AcceptedAt, no accepted row'; Pass = $false
       Backlog = @(Backlog 'ONBOARD_DEPARTURE_UNSAFE' $null $laterRound); Accepted = @() }
    @{ Case = 'ACCEPTED written but AcceptedAt never set'; Pass = $false
       Backlog = @(Backlog 'ACCEPTED' $null $acceptedAt); Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'AcceptedAt is not this acceptance (one second off AcceptedDemands)'; Pass = $false
       Backlog = @(Backlog 'ACCEPTED' '2026-10-15 10:53:19.0348616+00:00' $laterRound); Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'backlog says accepted, AcceptedDemands has no row'; Pass = $false
       Backlog = @(Backlog 'ACCEPTED' $acceptedAt $acceptedAt); Accepted = @() }
    @{ Case = 'DEMAND_ALREADY_ACCEPTED with no later round to have written it'; Pass = $false
       Backlog = @(Backlog 'DEMAND_ALREADY_ACCEPTED' $acceptedAt $acceptedAt); Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'rewritten to a code other than DEMAND_ALREADY_ACCEPTED'; Pass = $false
       Backlog = @(Backlog 'DEMAND_DECISION_FACT_CHANGED' $acceptedAt $laterRound); Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'no backlog row at all'; Pass = $false
       Backlog = @(); Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'two backlog rows'; Pass = $false
       Backlog = @((Backlog 'ACCEPTED' $acceptedAt $acceptedAt), (Backlog 'ACCEPTED' $acceptedAt $acceptedAt))
       Accepted = @(Accepted $acceptedAt) }
    @{ Case = 'unparseable AcceptedAt'; Pass = $false
       Backlog = @(Backlog 'ACCEPTED' 'not a time' $acceptedAt); Accepted = @(Accepted 'not a time') }
)

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($case in $cases) {
    $result = Test-L2BacklogAcceptance -BacklogRows $case.Backlog -AcceptedRows $case.Accepted
    $line = "$(if ($result.Passed) { 'PASS' } else { 'FAIL' }) <- $($case.Case): $($result.Actual)"
    if ($result.Passed -ne $case.Pass) {
        $failures.Add("expected $(if ($case.Pass) { 'PASS' } else { 'FAIL' }), got $line")
    } else {
        Write-Host "ok    $line"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "WRONG $_" }
    throw "Test-L2BacklogAcceptance: $($failures.Count) of $($cases.Count) cases wrong."
}
Write-Host "Test-L2BacklogAcceptance: $($cases.Count) cases, all as expected."
