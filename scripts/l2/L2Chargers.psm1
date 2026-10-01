#Requires -Version 7
Set-StrictMode -Version Latest

# Batch 9-06's Chargers setup key and the charger roster import a charging scenario drives (control-server#404).
#
# By default a scenario has no charger at all: no station on the fake Map, nothing registered on the fake RIoT, and no
# charger roster version -- which the server reads as an empty roster. A vehicle that falls below its mandatory charge
# line there is put on the server's manual charging hold. A scenario about automatic charging says so:
#
#   Chargers = @(
#       @{ StationId = 211; StationName = '充电点1'; Node = 6 }
#   )
#
# That much is the orchestrator's, because it has to happen before anything starts: the station goes on the fake RIoT's
# Map (so the server's station catalog carries it under that name) and, with Node, on that node of the fake route graph
# (so the server's route graph reaches it; without Node the station is on no node and no vehicle can be sent to it).
#
# Everything else is the scenario's, because when it happens is what the scenario is about: registering the charger on the
# fake RIoT (PUT /chargers, which makes a finished charge order report CHARGING) and importing the roster -- with the
# charger, or empty -- through the site's own FieldOps verb (Invoke-L2ChargerRosterImport below).

<#
The charger stations this run puts on the fake Map, or $null for none. Each is an ordered table StationId, StationName,
Node (null for a station on no node).
#>
function Resolve-L2ChargerStations {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where
    )

    if (-not $Setup.ContainsKey('Chargers')) { return $null }
    $stations = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in @($Setup.Chargers)) {
        if ($entry -isnot [hashtable] -or -not $entry.ContainsKey('StationId') -or -not $entry.ContainsKey('StationName') -or
            [string]::IsNullOrWhiteSpace([string]$entry.StationName)) {
            throw "Every Chargers entry in $Where is a table with StationId and StationName (and Node to put it on the route graph)."
        }
        $stations.Add([ordered]@{
                StationId   = [int]$entry.StationId
                StationName = [string]$entry.StationName
                Node        = $(if ($entry.ContainsKey('Node')) { [int]$entry.Node } else { $null })
            })
    }
    if ($stations.Count -eq 0) { throw "Chargers in $Where is empty; leave the key out for no charger." }
    if (@($stations | ForEach-Object { $_.StationId } | Sort-Object -Unique).Count -ne $stations.Count) {
        throw "Chargers in $Where names a station twice."
    }
    # Two chargers may share a node: the server's route graph reaches every station on a node at that node's cost
    # (control-server#431 removed the one-station-per-node limit this used to guard against).
    return , $stations.ToArray()
}

<#
Imports one whole charger roster with the formal FieldOps verb, the way a site opens and closes a charging window:
the chargers given, or none (an empty roster is a version like any other). Writes the roster file and the Map's station
catalog (read from the fake RIoT's control plane, which does not count as a catalog read) into the evidence, and returns
the verb's JSON.

Each charger is a table with StationId and StationName; VehicleScope (VehicleKeys, empty for every vehicle) is optional.
#>
function Invoke-L2ChargerRosterImport {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Chargers,
        [Parameter(Mandatory)][int]$MapId,
        [Parameter(Mandatory)][string[]]$Fleet,
        [Parameter(Mandatory)][object]$Riot,
        [Parameter(Mandatory)][scriptblock]$InvokeFieldOps,
        [Parameter(Mandatory)][string]$SnapshotRoot,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$ChangeNote
    )

    $map = @($Riot.Snapshot().body.maps | Where-Object { [int]$_.mapId -eq $MapId })
    if ($map.Count -ne 1) { throw "The fake RIoT has no Map $MapId to take the station catalog from." }
    $catalogPath = Join-Path $SnapshotRoot "charger-roster-$Label-catalog-$MapId.json"
    [ordered]@{
        mapId    = $MapId
        stations = @($map[0].stations | ForEach-Object { [ordered]@{ stationId = [int]$_.id; stationName = [string]$_.name } })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $catalogPath -Encoding utf8NoBOM

    $rosterPath = Join-Path $SnapshotRoot "charger-roster-$Label.json"
    $entries = @($Chargers | ForEach-Object {
            [ordered]@{
                mapId          = $MapId
                stationId      = [int]$_.StationId
                stationName    = [string]$_.StationName
                entryStationId = $null
                exitStationId  = $null
                vehicleScope   = @($(if ($_.Contains('VehicleScope')) { $_.VehicleScope } else { @() }))
            }
        })
    $roster = [ordered]@{
        approvedBy    = 'L2 scenario'
        approvalBasis = 'scripts/l2/L2Chargers.psm1: a synthetic rig roster, never a field approval'
        changeNote    = $ChangeNote
        # An explicit array either way: ConvertTo-Json writes a one-element pipeline result as an object, and no element as null.
        chargers      = [object[]]$entries
    }
    [IO.File]::WriteAllText($rosterPath, (ConvertTo-Json -InputObject $roster -Depth 6), [Text.UTF8Encoding]::new($false))

    return & $InvokeFieldOps -Arguments @(
        'import-charger-roster', '--input', $rosterPath, '--catalog', $catalogPath,
        '--map', [string]$MapId, '--fleet', ($Fleet -join ';'))
}

Export-ModuleMember -Function Resolve-L2ChargerStations, Invoke-L2ChargerRosterImport
