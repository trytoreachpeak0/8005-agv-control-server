#Requires -Version 7
<#
.SYNOPSIS
Drives Wait-L2WorklistAcknowledgedAtOtherStation through the state the count-based criterion got wrong.

.DESCRIPTION
The shipped function, not a copy: Invoke-L2Query is replaced inside L2TaskTypeJourney's own module
scope, so Get-L2DemandJourneySnapshots parses real payload JSON on the way through and everything
above it runs as it runs on the rig.

The case this exists for is the first one. control-server#204 wrote the second stop's wait as "at
least 2 acknowledged worklists", which is a PROXY for "the second stop's worklist is acknowledged" --
equal to it only while every stop emits exactly one worklist. Revise the first stop's worklist and
the proxy is satisfied while the thing it stands for is not.

So that case asserts BOTH halves on the SAME input: the old criterion is satisfied, and the new one
is not. Asserting only the second half would not show the old one was ever wrong -- it would pass
just as well against a criterion that had always been right (control-server#265).

No real-rig round has ever been in any of these states: in both scenarios the callback that runs this
wait only fires after the server has sent the operation command, by which time the worklist is long
acknowledged. None of them can be reached by re-running a green scenario.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2TaskTypeJourney.psm1') -Force
$module = Get-Module L2TaskTypeJourney

# Module scope, not global: L2TaskTypeJourney.psm1 imports its dependencies itself, so its own scope
# wins over anything defined outside. Measured in control-server#203 -- a global stub is not reached.
& $module {
    function script:Invoke-L2Query {
        param($Connection, $Sql)
        $global:l2wsCalls++
        return $global:l2wsRows
    }
}

$results = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}

# A ProtocolOutbox row exactly as Get-L2DemandJourneySnapshots reads one, payload JSON included: the
# station id has to survive the real parse, not be handed to the criterion directly.
function Worklist([string]$id, [string]$station, [int]$revision, [bool]$acknowledged, [string]$demand = 'd-1') {
    $payload = @{ payload = @{ stationId = $station; worklistRevision = $revision; items = @(@{ demandId = $demand }) } }
    [pscustomobject]@{
        MessageId      = $id
        MessageType    = 'CurrentStopWorklistSnapshot'
        PayloadJson    = ($payload | ConvertTo-Json -Depth 8 -Compress)
        CreatedAt      = "2026-09-21T00:00:0$($id -replace '\D', '')Z"
        AcknowledgedAt = $(if ($acknowledged) { '2026-09-21T00:00:30Z' } else { $null })
        FencedAt       = $null
    }
}

function Invoke-Wait([object[]]$Rows, [string]$Previous) {
    $global:l2wsRows = $Rows
    $global:l2wsCalls = 0
    try {
        $v = Wait-L2WorklistAcknowledgedAtOtherStation -Connection 'stub' -DemandId 'd-1' `
            -PreviousStationId $Previous -Criterion 'destination-worklist-acknowledged' `
            -Description 'the onboard acknowledged the worklist at the second stop' -TimeoutSeconds 2
        return [pscustomobject]@{ Value = [string]$v; Error = $null; Calls = $global:l2wsCalls }
    } catch {
        return [pscustomobject]@{ Value = $null; Error = $_.Exception.Message; Calls = $global:l2wsCalls }
    }
}

# The criterion the old code used, evaluated with the same parser on the same rows. Not a hand count:
# the claim being made is about what the shipped reader would have produced.
function Get-OldCountCriterion([object[]]$Rows) {
    $global:l2wsRows = $Rows
    $global:l2wsCalls = 0
    # Parenthesised exactly as the shipped code was: the reader returns a single-layer array, and
    # `f | Where` hands Where-Object the whole list as ONE object while `(f) | Where` unrolls it.
    # Getting this wrong here would understate the old count and make the case below argue nothing.
    return @((Get-L2DemandJourneySnapshots 'stub' 'd-1') |
        Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.Acknowledged }).Count
}

# ---------------------------------------------------------------- the case this file exists for

$firstStopRevised = @(
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' 'STATION-A' 2 $true))

$oldCount = Get-OldCountCriterion $firstStopRevised
$new = Invoke-Wait -Rows $firstStopRevised -Previous 'STATION-A'
Add-Case '第一站清单改版：旧的条数判据被满足（>= 2），说明代理指标在这里就会放过' `
    ($oldCount -ge 2) "条数 = $oldCount"
Add-Case '第一站清单改版：新的站点判据不满足，等待超时而不是放过' `
    (($null -eq $new.Value -or $new.Value -eq '') -and $null -ne $new.Error -and $new.Error -like '*Timed out*') `
    "值=$($new.Value) 错误=$($new.Error)"

# ---------------------------------------------------------------- the ordinary states

$secondStopArrived = @(
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' 'STATION-B' 1 $true))
$ok = Invoke-Wait -Rows $secondStopArrived -Previous 'STATION-A'
Add-Case '第二站清单已确认：返回的是那一站的站点 id，不是布尔也不是条数' `
    ($ok.Value -ceq 'STATION-B') "值=$($ok.Value) 错误=$($ok.Error)"

$secondStopUnacknowledged = @(
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' 'STATION-B' 1 $false))
$pending = Invoke-Wait -Rows $secondStopUnacknowledged -Previous 'STATION-A'
Add-Case '第二站清单发了但没被确认：不接受' `
    ($null -ne $pending.Error -and $pending.Error -like '*Timed out*') "值=$($pending.Value) 错误=$($pending.Error)"

# An empty station id on the OTHER worklist must not be read as "different from STATION-A". Without
# the Test-L2RealPresent in the probe this row satisfies the criterion, because '' -ne 'STATION-A'.
$otherStationEmpty = @(
    (Worklist '1' 'STATION-A' 1 $true),
    (Worklist '2' '' 1 $true))
$empty = Invoke-Wait -Rows $otherStationEmpty -Previous 'STATION-A'
Add-Case '另一份清单的站点 id 为空：不算「不同的站点」' `
    ($null -ne $empty.Error -and $empty.Error -like '*Timed out*') "值=$($empty.Value) 错误=$($empty.Error)"

# ---------------------------------------------------------------- the guard on the input itself

# Asserted by whether the query ran, NOT by elapsed time: a wall-clock bound would be a different
# claim on a slow machine, while "the database was never read" is true by construction or not at all.
$vacuous = Invoke-Wait -Rows $secondStopArrived -Previous ''
Add-Case '前一站的站点 id 为空：抛错，而不是让判据退化成「任何清单都算」' `
    ($null -ne $vacuous.Error -and $vacuous.Error -like '*would satisfy the criterion*') `
    "错误=$($vacuous.Error)"
Add-Case '而且那个错抛在等待之外：一次数据库读都没有发生' `
    ($vacuous.Calls -eq 0) "探针读了 $($vacuous.Calls) 次"

Remove-Variable -Name l2wsRows, l2wsCalls -Scope Global -ErrorAction SilentlyContinue

$bad = 0
foreach ($r in $results) {
    if (-not $r.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($r.Ok) { 'ok  ' } else { 'BAD ' }), $r.Name, $r.Actual)
}
if ($bad -gt 0) {
    Write-Host "L2WorklistStationWait self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2WorklistStationWait self-check: all $($results.Count) cases as expected."
