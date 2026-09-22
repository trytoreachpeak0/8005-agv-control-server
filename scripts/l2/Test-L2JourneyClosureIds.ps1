#Requires -Version 7

<#
.SYNOPSIS
    Self-check for the journey closure messageId and the two L2 loading-phase readers that skip the
    closure business state by it (control-server#323; PR #329 review, low 3).

.DESCRIPTION
    Under a second, no rig, no database: Invoke-L2Query is replaced by a table the cases hand in.

    Since control-server#323 every journey closure stages a VehicleBusinessStateSnapshot without a
    loadingPhase. Get-L2LoadingPhaseSnapshots and Get-L2YieldSnapshotsOf used to throw on any business
    state without one, which was an implicit guard: a mid-journey snapshot that lost its loadingPhase
    turned the scenario red. They now skip exactly the closure snapshot, recognised by its messageId,
    and still throw on every other one. That messageId is derived in two places -- the server's
    JourneyClosure.SnapshotMessageIds and Get-L2JourneyClosureMessageId in CargoHoldingCommon.ps1 --
    so the same vector is pinned here and in
    JourneyClosureSnapshotTests.TheClosureMessageIdsAreStableAcrossTheServerAndTheL2Scripts.

    Cases:

      - the vector: journey 'cs323-vector-journey' gives the three pinned ids;
      - the closure business state (null loadingPhase, closure messageId) is skipped, the journey's
        own snapshots are returned in revision order, and another vehicle's are left out;
      - a business state with a null loadingPhase that is NOT the closure one throws, from both
        readers;
      - Get-L2LoadingPhaseSnapshots without an agvId throws instead of reading every vehicle.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2JourneyClosureIds.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
. (Join-Path $PSScriptRoot 'scenarios/StationYieldCommon.ps1')

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

# The readers call Invoke-L2Query; this script-scope function is found before the module's.
$script:Journeys = @()
$script:Outbox = @()
function Invoke-L2Query([object]$Connection, [string]$Sql) {
    if ($Sql -like '*FROM JourneyRuntimes*') { return , @($script:Journeys | ForEach-Object { [pscustomobject]@{ JourneyId = $_ } }) }
    if ($Sql -like '*FROM ProtocolOutbox*') { return , @($script:Outbox) }
    throw "Unexpected query in the self-check: $Sql"
}

function Business([string]$messageId, [string]$agvId, [long]$revision, [object]$phase) {
    $payload = [ordered]@{ vehicleBusinessStateRevision = $revision; loadingPhase = $phase }
    [pscustomobject]@{
        MessageId   = $messageId
        CreatedAt   = '2026-09-23T00:00:00.0000000+00:00'
        PayloadJson = (@{ agvId = $agvId; payload = $payload } | ConvertTo-Json -Depth 5 -Compress)
    }
}
function Phase([string]$state) { @{ state = $state; cargoHoldingDeadlineAt = $null; closedReason = $null } }

# 1. The vector, the same literals as the server-side test.
$expected = @(
    'closure-worklist=50480606-8dea-0a5f-b263-f1431116b778',
    'closure-plan=69b06cfd-7405-c157-a2f8-7509fec4491f',
    'closure-vehicle-business-state=cc104e51-9899-d15e-9363-b24f0337ca04')
$actual = @('closure-worklist', 'closure-plan', 'closure-vehicle-business-state' |
    ForEach-Object { "$_=$(Get-L2JourneyClosureMessageId 'cs323-vector-journey' $_)" })
Check 'closure messageIds match the server vector' (($actual -join ' ') -eq ($expected -join ' ')) ($actual -join ' ')

# 2. The closure snapshot is skipped, the rest kept in order, the other vehicle left out.
$journey = 'journey-a'
$closureId = Get-L2JourneyClosureMessageId $journey 'closure-vehicle-business-state'
$script:Journeys = @($journey)
$script:Outbox = @(
    (Business 'm-3' 'AGV-L2-001' 3 (Phase 'CLOSED')),
    (Business 'm-1' 'AGV-L2-001' 1 (Phase 'LOADING')),
    (Business 'm-9' 'AGV-L2-002' 9 (Phase 'LOADING')),
    (Business $closureId 'AGV-L2-001' 4 $null))
# Assigned the way the scenarios assign it: both readers return the array whole (return , @(...)), so an
# extra @() here would read one element that is the whole list.
$read = Get-L2LoadingPhaseSnapshots $null 'AGV-L2-001'
$seen = ($read | ForEach-Object { "$($_.Revision):$($_.State)" }) -join ' '
Check 'loading-phase reader skips the closure, filters by agvId, orders by revision' ($seen -eq '1:LOADING 3:CLOSED') "read '$seen'"
$yield = Get-L2YieldSnapshotsOf $null 'AGV-L2-001'
$seen = ($yield | ForEach-Object { "$($_.Revision):$($_.State)" }) -join ' '
Check 'yield reader skips the closure the same way' ($seen -eq '1:LOADING 3:CLOSED') "read '$seen'"

# 3. A null loadingPhase that is not the closure snapshot still fails, from both readers.
$script:Outbox = @(
    (Business 'm-1' 'AGV-L2-001' 1 (Phase 'LOADING')),
    (Business 'm-2' 'AGV-L2-001' 2 $null))
foreach ($reader in @(
        @{ Name = 'loading-phase reader'; Call = { Get-L2LoadingPhaseSnapshots $null 'AGV-L2-001' } },
        @{ Name = 'yield reader'; Call = { Get-L2YieldSnapshotsOf $null 'AGV-L2-001' } })) {
    $thrown = $null
    try { $null = & $reader.Call } catch { $thrown = $_.Exception.Message }
    Check "$($reader.Name) throws on a non-closure snapshot without loadingPhase" `
        ($null -ne $thrown -and $thrown -like '*m-2*has no loadingPhase*') "threw '$thrown'"
}

# 4. No agvId: refuse rather than read every vehicle's stream.
$thrown = $null
try { $null = Get-L2LoadingPhaseSnapshots $null '' } catch { $thrown = $_.Exception.Message }
Check 'loading-phase reader refuses an empty agvId' ($null -ne $thrown -and $thrown -like '*needs the agvId*') "threw '$thrown'"

if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures -join ', ')"
    exit 1
}
Write-Host 'All journey closure id checks passed.'
