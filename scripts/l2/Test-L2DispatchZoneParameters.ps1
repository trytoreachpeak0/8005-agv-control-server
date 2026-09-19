#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2DispatchZoneParameters.psm1: how the orchestrator reads batch 7's two setup keys before any
    process starts.

.DESCRIPTION
    Pure input, no rig and no database, about a second. Each case is a setup table as a scenario's setup.psd1 would
    give it; the accepted ones must come out as the value the orchestrator uses, the refused ones must throw before
    anything is started -- a misspelt key or value would otherwise leave the scenario on the default precondition while
    it claims the one it asked for. The database half -- the version written into a live server's store -- is shown by
    a local throwaway scenario in control-server#206's evidence.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2DispatchZoneParameters.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2DispatchZoneParameters.psm1') -Force

$failures = 0
function Test-Case {
    param([string]$Name, [scriptblock]$Body, [object]$Expected, [string]$Refusal)
    try {
        $actual = & $Body
        if ($Refusal) {
            Write-Host "FAIL  $Name -- expected a refusal matching '$Refusal', got '$($actual | ConvertTo-Json -Compress -Depth 5)'"
            $script:failures++
            return
        }
        $actualJson = $actual | ConvertTo-Json -Compress -Depth 5
        $expectedJson = $Expected | ConvertTo-Json -Compress -Depth 5
        if ($actualJson -ceq $expectedJson) {
            Write-Host "PASS  $Name"
        } else {
            Write-Host "FAIL  $Name -- expected $expectedJson, got $actualJson"
            $script:failures++
        }
    } catch {
        if ($Refusal -and $_.Exception.Message -match $Refusal) {
            Write-Host "PASS  $Name (refused: $($_.Exception.Message))"
        } else {
            Write-Host "FAIL  $Name -- $($_.Exception.Message)"
            $script:failures++
        }
    }
}

$where = 'case.setup.psd1'

# CargoHoldingTimeout
Test-Case 'no CargoHoldingTimeout passes nothing' { Resolve-L2CargoHoldingTimeout -Setup @{} -Where $where } $null
Test-Case 'forty seconds' { Resolve-L2CargoHoldingTimeout -Setup @{ CargoHoldingTimeout = '00:00:40' } -Where $where } '00:00:40'
Test-Case 'thirty minutes' { Resolve-L2CargoHoldingTimeout -Setup @{ CargoHoldingTimeout = '00:30:00' } -Where $where } '00:30:00'
Test-Case 'zero is refused' { Resolve-L2CargoHoldingTimeout -Setup @{ CargoHoldingTimeout = '00:00:00' } -Where $where } $null 'positive time span'
Test-Case 'a negative span is refused' { Resolve-L2CargoHoldingTimeout -Setup @{ CargoHoldingTimeout = '-00:00:40' } -Where $where } $null 'positive time span'
Test-Case 'seconds as a number are refused' { Resolve-L2CargoHoldingTimeout -Setup @{ CargoHoldingTimeout = 40 } -Where $where } $null 'positive time span'
Test-Case 'a misspelt time is refused' { Resolve-L2CargoHoldingTimeout -Setup @{ CargoHoldingTimeout = '40s' } -Where $where } $null 'positive time span'

# DispatchZoneParameters
Test-Case 'no DispatchZoneParameters writes nothing' { Resolve-L2DispatchZoneParameters -Setup @{} -Where $where } $null
Test-Case 'two zones, ordered by name, zero kept apart from unconfigured' {
    Resolve-L2DispatchZoneParameters -Where $where -Setup @{
        DispatchZoneParameters = @{
            'ZONE-B' = @{ EnRouteAdditionMaxPathCostIncrease = 0; StarvationThresholdSeconds = $null }
            'ZONE-A' = @{ EnRouteAdditionMaxPathCostIncrease = 20000; StarvationThresholdSeconds = 600 }
        }
    }
} @(
    [pscustomobject][ordered]@{ DispatchZone = 'ZONE-A'; EnRouteAdditionMaxPathCostIncrease = 20000; StarvationThresholdSeconds = 600 }
    [pscustomobject][ordered]@{ DispatchZone = 'ZONE-B'; EnRouteAdditionMaxPathCostIncrease = 0; StarvationThresholdSeconds = $null }
)
Test-Case 'a field left out is unconfigured' {
    Resolve-L2DispatchZoneParameters -Where $where -Setup @{ DispatchZoneParameters = @{ 'ZONE-A' = @{ StarvationThresholdSeconds = 60 } } }
} @([pscustomobject][ordered]@{ DispatchZone = 'ZONE-A'; EnRouteAdditionMaxPathCostIncrease = $null; StarvationThresholdSeconds = 60 })
Test-Case 'an empty table is refused' { Resolve-L2DispatchZoneParameters -Setup @{ DispatchZoneParameters = @{} } -Where $where } $null 'non-empty table'
Test-Case 'a zone without a table is refused' {
    Resolve-L2DispatchZoneParameters -Setup @{ DispatchZoneParameters = @{ 'ZONE-A' = 20000 } } -Where $where
} $null 'no table'
Test-Case 'a misspelt field is refused' {
    Resolve-L2DispatchZoneParameters -Setup @{ DispatchZoneParameters = @{ 'ZONE-A' = @{ EnRouteAdditionMaxCostIncrease = 1 } } } -Where $where
} $null 'EnRouteAdditionMaxCostIncrease'
Test-Case 'a negative value is refused' {
    Resolve-L2DispatchZoneParameters -Setup @{ DispatchZoneParameters = @{ 'ZONE-A' = @{ StarvationThresholdSeconds = -1 } } } -Where $where
} $null 'non-negative whole number'
Test-Case 'a time written as text is refused' {
    Resolve-L2DispatchZoneParameters -Setup @{ DispatchZoneParameters = @{ 'ZONE-A' = @{ StarvationThresholdSeconds = '00:10:00' } } } -Where $where
} $null 'non-negative whole number'
Test-Case 'a fractional value is refused' {
    Resolve-L2DispatchZoneParameters -Setup @{ DispatchZoneParameters = @{ 'ZONE-A' = @{ EnRouteAdditionMaxPathCostIncrease = 1.5 } } } -Where $where
} $null 'non-negative whole number'

# Keys that look like batch 7's but are not spelled like either.
foreach ($misspelt in @('DispatchZoneParameter', 'dispatchZoneParameters', 'DispatchZoneParamters', 'Dispatch_Zone_Parameters', 'CargoHoldTimeout', 'cargoHoldingTimeout')) {
    Test-Case "the key '$misspelt' is refused" {
        Resolve-L2DispatchZoneParameters -Setup @{ $misspelt = 1 } -Where $where
    }.GetNewClosure() $null 'Unknown setup key'
}
# Keys of later tickets that only share a word with batch 7's are not near misses.
foreach ($later in @('CargoHoldingYieldWindow', 'LoadingPhaseClosedReason', 'ZoneParameters', 'StarvationEscalation')) {
    Test-Case "the key '$later' passes" {
        Resolve-L2DispatchZoneParameters -Setup @{ $later = 1 } -Where $where
    }.GetNewClosure() $null
}
Test-Case 'unrelated keys pass untouched' {
    Resolve-L2CargoHoldingTimeout -Setup @{ StationDepartureWaitTimeout = '00:00:10'; AreaAssignments = $false } -Where $where
} $null

Write-Host ''
if ($failures -gt 0) {
    Write-Host "$failures case(s) came out the other way."
    exit 1
}
Write-Host 'Every case came out as expected.'
