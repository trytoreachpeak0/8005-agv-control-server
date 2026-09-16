#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2SlotGroups.psm1's group assertion: one constructed case that must pass, and constructed
    cases that must each fail for their own reason.

.DESCRIPTION
    Pure input, no rig and no database, a few seconds. The model here is deliberately not today's approved one:
    FRONT is the even slots and REAR the odd ones, so a helper that had "1-4 is FRONT" written into it would get
    every case below wrong. The database half -- reading a real vehicle's groups and available slots -- is
    exercised against a running server by the L2 evidence in control-server#71.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2SlotGroups.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2SlotGroups.psm1') -Force

$positions = [System.Collections.Generic.SortedDictionary[int, string]]::new()
foreach ($slot in 1..8) { $positions[$slot] = if ($slot % 2 -eq 0) { 'FRONT' } else { 'REAR' } }
# Slot 2 is occupied, so FRONT has 4, 6 and 8 available.
$available = @(1, 3, 4, 5, 6, 7, 8)

$cases = @(
    @{ Name = 'the lowest two available FRONT slots, ascending'; Targets = @(4, 6); Pass = $true; Reason = $null }
    @{ Name = 'every available FRONT slot'; Targets = @(4, 6, 8); Pass = $true; Reason = $null }
    @{ Name = 'a REAR slot among the targets'; Targets = @(3, 4); Pass = $false; Reason = 'not in group' }
    @{ Name = 'an unavailable FRONT slot'; Targets = @(2, 4); Pass = $false; Reason = 'not the lowest-numbered' }
    @{ Name = 'FRONT slots, but not the lowest ones'; Targets = @(6, 8); Pass = $false; Reason = 'not the lowest-numbered' }
    @{ Name = 'the right slots in descending order'; Targets = @(6, 4); Pass = $false; Reason = 'not strictly ascending' }
    @{ Name = 'one slot twice'; Targets = @(4, 4); Pass = $false; Reason = 'not strictly ascending' }
    @{ Name = 'more targets than the group has available'; Targets = @(2, 4, 6, 8); Pass = $false; Reason = 'only 3 available' }
    @{ Name = 'no targets at all'; Targets = @(); Pass = $false; Reason = 'no target slots' }
)

$wrong = 0
foreach ($case in $cases) {
    $result = Test-L2SlotGroupTargets -TargetSlots $case.Targets -SlotPositions $positions `
        -SlotPosition 'FRONT' -AvailableSlots $available
    $asExpected = ($result.Passed -eq $case.Pass) -and
        ($case.Pass -or ([string]$result.Reason).Contains($case.Reason))
    if (-not $asExpected) { $wrong++ }
    $verdict = if ($result.Passed) { 'PASS' } else { "FAIL ($($result.Reason))" }
    Write-Host ("{0}  [{1}] {2} -> {3}" -f $(if ($asExpected) { 'ok  ' } else { 'BAD ' }),
        ($case.Targets -join ','), $case.Name, $verdict)
}

if ($wrong -gt 0) {
    Write-Host "L2SlotGroups self-check: $wrong of $($cases.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2SlotGroups self-check: all $($cases.Count) cases as expected (2 pass, $($cases.Count - 2) fail)."
