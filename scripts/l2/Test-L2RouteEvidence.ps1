#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2RouteEvidence.psm1: the pwsh recompute of a route evidence id agrees with the
    server's, and every input it claims to hash actually reaches the hash.

.DESCRIPTION
    Pure input, no rig, well under a second.

    `G3-11-07` of g3-reversed-direction-journey used to assert only that
    `JourneyRuntimes.RouteEvidenceId` was non-empty, which a server that hashed the two ends the wrong
    way round would pass. It now recomputes the id and compares, and that turns a second implementation
    into something the criterion depends on -- so this file guards the second implementation.

    Two of the cases are the ones that stop the two copies drifting apart. They assert the pairs the C#
    tests pin, on the same inputs:

      - the catalog fingerprint, from `HttpRiotMovementGatewayTests`
        (`StrictMapCatalogIsCanonicalAndObservedAfterSdkReadCompletes`);
      - the route evidence id, from
        `JourneyPlanCharacterizationTests.TheRouteEvidenceIdOfAWireToGateCandidateIsPinned`.

    Both of those tests carry a remark saying this copy is pinned to them, because their going red is
    the only notice it would get.

    The rest are the reverse guard, and they are the ones with teeth. A recompute that silently dropped
    an input would still agree with a pinned pair as long as the dropped input happened to hold its
    pinned value -- so every one of the eight inputs is perturbed on its own and has to move the id, and
    the eight perturbed ids have to differ from each other as well. Same for the Map id and both parts
    of a station row inside the fingerprint. Without those, a copy that hashed, say, the origin twice
    and the destination never would pass every pinned pair written with this fixture.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2RouteEvidence.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2RouteEvidence.psm1') -Force

# The Map HttpRiotMovementGatewayTests serves, and the fingerprint it asserts for it.
$pinnedStations = @(
    [pscustomobject]@{ Id = 210; Name = '关卡' }
    [pscustomobject]@{ Id = 12; Name = 'N1-3_N1-7' }
    [pscustomobject]@{ Id = 11; Name = 'C15-13' })
$pinnedCatalogSha = '8e1df56b366705969098327145588a67612a05b172c8958aa1f0aec64f3e6d30'

# The other pinned pair, from JourneyPlanCharacterizationTests.TheRouteEvidenceIdOfAWireToGateCandidateIsPinned:
# a WIRE_TO_GATE route, AREA pickup station first and the gate second. The fingerprint there is a made-up
# string rather than one taken from a station list, and that is fine -- the two halves are pinned
# separately, and this half only has to hash whatever fingerprint it is handed.
$forwardId = 'MAPCAT-0aa0f18581b7398e15375d080e9ed6af20005814663877de20c1a28e6bfa9739'
$baseline = [ordered]@{
    MapId                    = 25
    CatalogSha256            = '5f0c3e4d2a1b09876543210fedcba9876543210fedcba9876543210fedcba98'
    OriginStationRiotId      = 12
    OriginStationName        = 'N1-1'
    DestinationStationRiotId = 210
    DestinationStationName   = '关卡'
    Area                     = 'N1-1'
    Eqp                      = 'EQP-01'
}
# One different value per input, each a value the real system could plausibly hold, and all eight
# distinct from one another so that two perturbations colliding means something.
$otherValues = @{
    MapId                    = 26
    CatalogSha256            = ('6' + $baseline.CatalogSha256.Substring(1))
    OriginStationRiotId      = 11
    OriginStationName        = 'N1-9'
    DestinationStationRiotId = 211
    DestinationStationName   = '关卡二'
    Area                     = 'N1-3'
    Eqp                      = 'EQP-02'
}

function Get-IdWith([hashtable]$Overrides) {
    $arguments = @{}
    foreach ($key in $baseline.Keys) { $arguments[$key] = $baseline[$key] }
    foreach ($key in $Overrides.Keys) { $arguments[$key] = $Overrides[$key] }
    return Get-L2RouteEvidenceId @arguments
}

$cases = @()

$cases += @{
    Name  = 'the catalog fingerprint is the one HttpRiotMovementGatewayTests pins'
    Check = {
        $actual = Get-L2MapCatalogSha256 -MapId 25 -Stations $pinnedStations
        @{ Ok = ($actual -ceq $pinnedCatalogSha); Actual = $actual }
    }
}

$cases += @{
    Name  = 'the fingerprint does not depend on the order the stations arrive in'
    Check = {
        $reordered = @($pinnedStations[1], $pinnedStations[2], $pinnedStations[0])
        $actual = Get-L2MapCatalogSha256 -MapId 25 -Stations $reordered
        @{ Ok = ($actual -ceq $pinnedCatalogSha); Actual = $actual }
    }
}

$cases += @{
    Name  = 'the route evidence id is the one JourneyPlanCharacterizationTests pins'
    Check = {
        $actual = Get-IdWith @{}
        @{ Ok = ($actual -ceq $forwardId); Actual = $actual }
    }
}

$cases += @{
    # The pinned pair fixes the forward direction; this fixes that the hash is sensitive to it at all.
    # Without it a copy that hashed the ends as an unordered set would pass every pinned pair, and the
    # criterion that recompute serves -- telling a swapped route from the planned one -- would be vacuous.
    Name  = 'swapping the two ends moves the id'
    Check = {
        $actual = Get-IdWith @{
            OriginStationRiotId      = $baseline.DestinationStationRiotId
            OriginStationName        = $baseline.DestinationStationName
            DestinationStationRiotId = $baseline.OriginStationRiotId
            DestinationStationName   = $baseline.OriginStationName
        }
        @{ Ok = ($actual -cne $forwardId); Actual = $actual }
    }
}

$cases += @{
    Name  = 'every one of the eight inputs reaches the id, and no two of them collide'
    Check = {
        # Compared against what this implementation returns for the baseline, NOT against $forwardId.
        # Drop one input and every perturbation still differs from the pinned id, so a comparison
        # against the pinned value reports nothing here and leans entirely on the pinned-pair cases --
        # measured: mutating the module to drop Eqp left this case green while those two went red.
        $current = Get-IdWith @{}
        $moved = [ordered]@{}
        foreach ($key in $baseline.Keys) { $moved[$key] = Get-IdWith @{ $key = $otherValues[$key] } }
        $unmoved = @($moved.Keys | Where-Object { $moved[$_] -ceq $current })
        $distinct = @($moved.Values | Sort-Object -Unique -CaseSensitive).Count
        $detail = if ($unmoved.Count) { " ($($unmoved -join ', '))" } else { '' }
        @{ Ok     = ($unmoved.Count -eq 0 -and $distinct -eq $baseline.Count)
           Actual = "$($unmoved.Count) input(s) absent from the hash$detail / $($baseline.Count) perturbations gave $distinct distinct ids" }
    }
}

$cases += @{
    Name  = 'the Map id and both parts of every station row reach the fingerprint'
    Check = {
        $renamed = @(
            [pscustomobject]@{ Id = 210; Name = '关卡' }
            [pscustomobject]@{ Id = 12; Name = 'N1-3_N1-8' }
            [pscustomobject]@{ Id = 11; Name = 'C15-13' })
        $renumbered = @(
            [pscustomobject]@{ Id = 211; Name = '关卡' }
            [pscustomobject]@{ Id = 12; Name = 'N1-3_N1-7' }
            [pscustomobject]@{ Id = 11; Name = 'C15-13' })
        $variants = [ordered]@{
            'map id'       = (Get-L2MapCatalogSha256 -MapId 26 -Stations $pinnedStations)
            'station name' = (Get-L2MapCatalogSha256 -MapId 25 -Stations $renamed)
            'station id'   = (Get-L2MapCatalogSha256 -MapId 25 -Stations $renumbered)
        }
        # Against this implementation's own baseline, for the reason spelled out in the case above.
        $current = Get-L2MapCatalogSha256 -MapId 25 -Stations $pinnedStations
        $unmoved = @($variants.Keys | Where-Object { $variants[$_] -ceq $current })
        $distinct = @($variants.Values | Sort-Object -Unique -CaseSensitive).Count
        $detail = if ($unmoved.Count) { " ($($unmoved -join ', '))" } else { '' }
        @{ Ok     = ($unmoved.Count -eq 0 -and $distinct -eq $variants.Count)
           Actual = "$($unmoved.Count) part(s) absent from the fingerprint$detail / $($variants.Count) perturbations gave $distinct distinct fingerprints" }
    }
}

$cases += @{
    Name  = 'a station this journey never visits still moves every id on that Map'
    Check = {
        $withExtra = @($pinnedStations) + [pscustomobject]@{ Id = 300; Name = '等待点' }
        $sha = Get-L2MapCatalogSha256 -MapId 25 -Stations $withExtra
        $actual = Get-IdWith @{ CatalogSha256 = $sha }
        @{ Ok     = ($sha -cne $pinnedCatalogSha -and $actual -cne $forwardId)
           Actual = "fingerprint $($sha.Substring(0, 12))... / id $($actual.Substring(7, 12))..." }
    }
}

$cases += @{
    Name  = 'an empty catalog is refused rather than hashed to a fixed value'
    Check = {
        try {
            $actual = Get-L2MapCatalogSha256 -MapId 25 -Stations @()
            @{ Ok = $false; Actual = "returned $actual" }
        } catch {
            @{ Ok = ($_.Exception.Message -like '*at least one station*'); Actual = "threw: $($_.Exception.Message)" }
        }
    }
}

$cases += @{
    Name  = 'a catalog with two rows for one station id is refused'
    Check = {
        $duplicated = @($pinnedStations) + [pscustomobject]@{ Id = 12; Name = 'N1-9' }
        try {
            $actual = Get-L2MapCatalogSha256 -MapId 25 -Stations $duplicated
            @{ Ok = $false; Actual = "returned $actual" }
        } catch {
            @{ Ok = ($_.Exception.Message -like '*duplicate station ids*'); Actual = "threw: $($_.Exception.Message)" }
        }
    }
}

$wrong = 0
foreach ($case in $cases) {
    try { $result = & $case.Check } catch { $result = @{ Ok = $false; Actual = "threw: $($_.Exception.Message)" } }
    if (-not $result.Ok) { $wrong++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($result.Ok) { 'ok  ' } else { 'BAD ' }), $case.Name, $result.Actual)
}

if ($wrong -gt 0) {
    Write-Host "L2RouteEvidence self-check: $wrong of $($cases.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2RouteEvidence self-check: all $($cases.Count) cases as expected."
