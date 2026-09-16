#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2SlotGroups.psm1's group assertion: constructed cases that must pass, constructed cases that
    must each fail for their own reason, and the assertion's default and explicit description.

.DESCRIPTION
    Pure input, no rig and no database, a few seconds. The model here is deliberately not today's approved one:
    FRONT is the even slots and REAR the odd ones, so a helper that had "1-4 is FRONT" written into it would get
    every case below wrong. The database half -- reading a real vehicle's groups and available slots -- is
    exercised against a running server by the L2 evidence in control-server#71.

    It also checks the text Assert-L2SlotGroupTargets writes into the evidence row, with and without -Description,
    by replacing that function's two database reads inside the module with the same constructed model.

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

# Assert-L2SlotGroupTargets' assertion text. Its database reads are replaced inside the module with the model
# above, so this still needs no database; what it checks is only which 判据 lands in the evidence row.
$slotGroups = Get-Module L2SlotGroups
& $slotGroups {
    param($positions)
    $script:stubPositions = $positions
    $script:stubJourneys = @()
    function script:Invoke-L2Query([object]$Connection, [string]$Sql) { return , @($script:stubJourneys) }
    function script:Get-L2VehicleSlotPositions([object]$Connection, [string]$AgvId) {
        return [pscustomobject]@{ AgvId = $AgvId; Positions = $script:stubPositions }
    }
} $positions

$descriptionCases = @(
    @{ Name = 'no journey, no -Description'; Journey = $false; Arguments = @{}
       Expected = '需求 D-1 的目标仓位属于 FRONT 组' }
    @{ Name = 'no journey, -Description'; Journey = $false; Arguments = @{ Description = '自定义判据' }
       Expected = '自定义判据' }
    @{ Name = 'journey, no -Description'; Journey = $true; Arguments = @{}
       Expected = '需求 D-1 的目标仓位全部属于 FRONT 组、升序，且恰好是该组编号最小的 2 个可用仓' }
    @{ Name = 'journey, -Description'; Journey = $true; Arguments = @{ Description = '自定义判据' }
       Expected = '自定义判据' }
)
foreach ($case in $descriptionCases) {
    & $slotGroups {
        param($journey)
        $script:stubJourneys = $journey ? @([pscustomobject]@{ AgvId = 'agv-1'; TargetSlotsJson = '[4,6]' }) : @()
    } $case.Journey
    $assertions = & $slotGroups { New-L2Assertions }
    $arguments = $case.Arguments
    $null = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'SELF-CHECK' -Connection ([pscustomobject]@{}) -DemandId 'D-1' `
        -SlotPosition 'FRONT' -AvailableSlots $available @arguments
    $actual = [string]$assertions.Items[0].description
    $asExpected = $actual -ceq $case.Expected
    if (-not $asExpected) { $wrong++ }
    Write-Host ("{0}  {1} -> description '{2}'" -f $(if ($asExpected) { 'ok  ' } else { 'BAD ' }), $case.Name, $actual)
}

$total = $cases.Count + $descriptionCases.Count
if ($wrong -gt 0) {
    Write-Host "L2SlotGroups self-check: $wrong of $total cases came out the wrong way."
    exit 1
}
Write-Host ("L2SlotGroups self-check: all $total cases as expected (2 pass, $($cases.Count - 2) fail, " +
    "$($descriptionCases.Count) assertion descriptions).")
