#Requires -Version 7
Set-StrictMode -Version Latest

<#
An independent recompute of a journey's route evidence id, for assertions that have to tell the
planned route from a swapped one.

`JourneyRuntimes.RouteEvidenceId` is what an idempotent replay is matched on, and the server derives
it from eight inputs in a fixed order (`MapStationResolver.BuildRouteEvidenceId`). Asserting that the
stored value is merely non-empty says nothing about any of them: a server that hashed the two ends
the wrong way round, dropped an input or changed the spelling still stores something non-empty. So a
scenario recomputes the id here, from the station catalog the fake RIoT served and the ends it asked
for, and compares.

The two halves of the recompute are pinned on the C# side, and `Test-L2RouteEvidence.ps1` asserts the
same two pairs against this copy:

- stations to fingerprint, by `HttpRiotMovementGatewayTests`
  (`StrictMapCatalogIsCanonicalAndObservedAfterSdkReadCompletes`);
- fingerprint to id, by
  `JourneyPlanCharacterizationTests.TheRouteEvidenceIdOfAWireToGateCandidateIsPinned`.

Change either spelling on the server and its own test goes red, which is the notice to move this
file with it. Nothing else would give one -- this module is not built and no other test reads it.
#>

function ConvertTo-L2Sha256Hex {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    return [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

<#
The catalog fingerprint `HttpRiotMovementGateway.ReadMapStationsAsync` computes over the whole Map:
one line per station, ordered by station id, `mapId<TAB>stationId<TAB>stationName`, joined by LF.

Every station on the Map goes in, not just the two this journey uses -- an unrelated station moving
changes the fingerprint and so changes every route evidence id on that Map, which is the point of
hashing the catalog rather than the endpoints alone.
#>
function Get-L2MapCatalogSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$MapId,
        # AllowEmptyCollection so that an empty catalog reaches the check below and is refused with a
        # reason. Without it the parameter binder rejects it first, with a message about argument
        # binding -- a red whose text sends the reader to the call site rather than to the catalog.
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Stations)

    if ($Stations.Count -eq 0) {
        throw 'A Map station catalog fingerprint needs at least one station; the server refuses an empty catalog.'
    }
    $ids = @($Stations | ForEach-Object { [int]$_.Id })
    if (@($ids | Sort-Object -Unique).Count -ne $ids.Count) {
        throw "Map $MapId station catalog has duplicate station ids; the server refuses that catalog rather than hashing it."
    }
    $lines = @($Stations | Sort-Object -Property @{ Expression = { [int]$_.Id } } |
        ForEach-Object { "$MapId`t$([int]$_.Id)`t$([string]$_.Name)" })
    return ConvertTo-L2Sha256Hex ($lines -join "`n")
}

<#
`MapStationResolver.BuildRouteEvidenceId`: the eight inputs joined by `|` in this order, hashed, and
prefixed. The origin is hashed first and the destination second, which is the whole reason a swapped
route gets a different id.
#>
function Get-L2RouteEvidenceId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$MapId,
        [Parameter(Mandatory)][string]$CatalogSha256,
        [Parameter(Mandatory)][int]$OriginStationRiotId,
        [Parameter(Mandatory)][string]$OriginStationName,
        [Parameter(Mandatory)][int]$DestinationStationRiotId,
        [Parameter(Mandatory)][string]$DestinationStationName,
        [Parameter(Mandatory)][string]$Area,
        [Parameter(Mandatory)][string]$Eqp)

    $canonical = @(
        [string]$MapId,
        $CatalogSha256,
        [string]$OriginStationRiotId,
        $OriginStationName,
        [string]$DestinationStationRiotId,
        $DestinationStationName,
        $Area,
        $Eqp) -join '|'
    return "MAPCAT-$(ConvertTo-L2Sha256Hex $canonical)"
}

Export-ModuleMember -Function Get-L2MapCatalogSha256, Get-L2RouteEvidenceId
