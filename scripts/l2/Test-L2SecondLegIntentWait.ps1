#Requires -Version 7
<#
.SYNOPSIS
Drives Wait-L2SecondLegIntent through the orderings the rig never reaches.

.DESCRIPTION
The shipped function, not a copy of it: Invoke-L2Query is replaced inside L2TaskTypeJourney's own
module scope, and everything above it runs as it runs on the rig. A structural copy would be a
different thing than what ships -- measured, when the first attempt at one put the probe outside the
module, removing the very cross-module resolution the test existed to check, and came out green on
code that was broken.

Every journey the rig has ever run carried exactly one second-leg intent, so all but the first case
below describe states no real-rig round has ever been in, and none of them can be reached by re-running
a green scenario. The last case is the one worth naming: when the wait has already failed, the
function reads once more to say something better than "timed out" -- and that read may not itself
become the failure. A server that is gone is one of the reasons the wait timed out in the first place.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'L2TaskTypeJourney.psm1') -Force
$module = Get-Module L2TaskTypeJourney

# Module scope, not global: L2TaskTypeJourney.psm1 imports L2.psm1 itself, so its own scope wins over
# anything defined outside. Measured -- a global stub of this name is simply not reached.
& $module {
    function script:Invoke-L2Query {
        param($Connection, $Sql)
        $global:l2StubLastSql = $Sql
        # A deadline rather than a call count: the read that matters is the one AFTER the wait gave up,
        # and "after the deadline" is exactly what that read is, by construction. A poll that happens
        # to fall on the wrong side throws too, which changes nothing -- Wait-L2Condition swallows it
        # and the wait times out either way. Nothing here depends on how many polls fitted in.
        if ($null -ne $global:l2StubThrowAfterUtc -and [DateTime]::UtcNow -gt $global:l2StubThrowAfterUtc) {
            throw [System.InvalidOperationException]::new('the database went away')
        }
        return $global:l2StubRows
    }
}

function Row([string]$upper, [string]$status, [string]$purpose) {
    [pscustomobject]@{ UpperId = $upper; OrderId = "o-$upper"; Status = $status; Purpose = $purpose }
}

$confirmedOnly = @((Row 'u1' 'CONFIRMED' 'TO_DROPOFF'))
$twoFirstConfirmed = @((Row 'u1' 'CONFIRMED' 'TO_DROPOFF'), (Row 'u2' 'CONFIRMED' 'TO_PARK'))
$twoSecondConfirmed = @((Row 'u1' 'PENDING' 'TO_DROPOFF'), (Row 'u2' 'CONFIRMED' 'TO_PARK'))

$cases = @(
    @{ Name     = 'one confirmed intent is returned'
       Rows     = $confirmedOnly
       Expected = 'returns u1' }

    @{ Name     = 'two intents, the first confirmed: the count is refused, not judged'
       Rows     = $twoFirstConfirmed
       Expected = 'throws multi' }

    # Without the check on the failure path this one is a bare timeout: the probe only ever looks at
    # the first row, so it answers $null for the whole 2 s and the reason never reaches the report.
    @{ Name     = 'two intents, only the later one confirmed: still refused, not a bare timeout'
       Rows     = $twoSecondConfirmed
       Expected = 'throws multi' }

    @{ Name     = 'no intent yet: the ordinary timeout, and no talk of a second leg'
       Rows     = @()
       Expected = 'throws timeout' }

    @{ Name     = 'one unconfirmed intent: the ordinary timeout'
       Rows     = @((Row 'u1' 'PENDING' 'TO_DROPOFF'))
       Expected = 'throws timeout' }

    # Without the inner try/catch on the failure path this one comes out as "throws other: the
    # database went away" -- the timeout, which is the only message that says WHAT was being waited
    # for, is gone. Reverse-verified: removing that try/catch turns exactly this case red.
    @{ Name        = 'the read after a failed wait may not replace the failure'
       Rows        = @()
       ThrowAfter  = 1
       Expected    = 'throws timeout' }
)

$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $global:l2StubRows = $case.Rows
    $global:l2StubThrowAfterUtc = if ($case.ContainsKey('ThrowAfter')) {
        [DateTime]::UtcNow.AddSeconds($case.ThrowAfter)
    } else { $null }

    $actual = $null
    try {
        $returned = Wait-L2SecondLegIntent -Connection 'conn' -DemandId 'DEMAND-1' -TimeoutSeconds 1
        $actual = "returns $($returned.UpperId)"
    } catch {
        $message = $_.Exception.Message
        $actual = if ($message -like '*non-TO_PICKUP order intents*') { 'throws multi' }
                  elseif ($message -like 'Timed out after*') { 'throws timeout' }
                  else { "throws other: $message" }
    }
    $results.Add([pscustomobject]@{ Name = $case.Name; Ok = ($actual -ceq $case.Expected); Actual = $actual })
}

# The multi-intent message has to name both intents, or it sends the reader looking at one of them.
$global:l2StubRows = $twoFirstConfirmed
$global:l2StubThrowAfterUtc = $null
$multiMessage = try { $null = Wait-L2SecondLegIntent -Connection 'conn' -DemandId 'DEMAND-1' -TimeoutSeconds 1; '(did not throw)' }
                catch { $_.Exception.Message }
$namesBoth = $multiMessage -like '*TO_DROPOFF/u1*' -and $multiMessage -like '*TO_PARK/u2*'
$results.Add([pscustomobject]@{
    Name = 'the refusal names every intent it found'
    Ok = $namesBoth
    Actual = $multiMessage })

# ORDER BY is what makes "the first row" mean anything at all; SQLite promises no order without it.
$results.Add([pscustomobject]@{
    Name = 'the query orders by UpperId'
    Ok = ($global:l2StubLastSql -like '*ORDER BY UpperId*')
    Actual = [string]$global:l2StubLastSql })

$bad = 0
foreach ($r in $results) {
    if (-not $r.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($r.Ok) { 'ok  ' } else { 'BAD ' }), $r.Name, $r.Actual)
}
if ($bad -gt 0) {
    Write-Host "L2SecondLegIntentWait self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2SecondLegIntentWait self-check: all $($results.Count) cases as expected."
