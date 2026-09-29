#Requires -Version 7
Set-StrictMode -Version Latest

# Batch 8's WaitingPoints setup key and default precondition (control-server#388, specification 5.4).
#
# Since control-server#388 a server with more than one vehicle in JourneyRuntime:Fleet refuses to start unless its
# waiting point registration gives every vehicle a waiting point of its own on the server's Map. So the default for a
# Fleet scenario is "as many waiting points as vehicles": the orchestrator adds stations 214, 215, ... (等待点1, 等待点2, ...
# -- the ids and names the site built on Map 26) to the fake RIoT's Map, and registers them through the formal FieldOps
# import before the server starts. No existing setup.psd1 changes for it.
#
# Registering waiting points moves no vehicle: until control-server#389 turns idle return on, nothing reads the
# registration at runtime except the startup check.
#
# The registration has to exist before the server's first start, and the database only exists once the server has
# migrated it. So the orchestrator runs the host once with --migrate-only (migrate, then exit) and imports into that
# database, the way a site's first multi-vehicle deployment runs: first start refused (or --migrate-only), import, start.
#
# The WaitingPoints key:
#   absent        the default above for a Fleet scenario; nothing for a single-vehicle one
#   $false        register nothing (a Fleet scenario then expects the startup refusal)
#   an integer N  N default waiting points, 214 .. 214+N-1
#   a list        explicit points: @{ StationId = 214; StationName = '等待点1'; Enabled = $true; VehicleScope = @('VK') }
#                 StationName defaults to 等待点<k>, Enabled to $true, VehicleScope to every vehicle (empty)
#                 Node (control-server#389) puts the station on that node of the fake RIoT's route graph; without it
#                 the station is on no node, which an idle return reads as unreachable

$script:FirstWaitingPointStation = 214

<#
The waiting points this run registers, or $null for none. Each is an ordered table StationId, StationName, Enabled,
VehicleScope (an array of VehicleKeys, empty for every vehicle).
#>
function Resolve-L2WaitingPoints {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where,
        [Parameter(Mandatory)][int]$FleetCount
    )

    if (-not $Setup.ContainsKey('WaitingPoints')) {
        if ($FleetCount -le 1) { return $null }
        return , @(New-L2DefaultWaitingPoints -Count $FleetCount)
    }
    $setting = $Setup.WaitingPoints
    if ($setting -is [bool]) {
        if ($setting) {
            throw "WaitingPoints = `$true in $Where means nothing: leave the key out for the default, give `$false for none, a count, or the points."
        }
        return $null
    }
    if ($setting -is [int]) {
        if ($setting -lt 1) { throw "WaitingPoints in $Where is a count of at least 1, or `$false for none." }
        return , @(New-L2DefaultWaitingPoints -Count $setting)
    }

    $points = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($entry in @($setting)) {
        $index++
        if ($entry -isnot [hashtable] -or -not $entry.ContainsKey('StationId')) {
            throw "Every WaitingPoints entry in $Where is a table with at least StationId."
        }
        $points.Add([ordered]@{
                StationId    = [int]$entry.StationId
                StationName  = $(if ($entry.ContainsKey('StationName')) { [string]$entry.StationName } else { "等待点$index" })
                Enabled      = $(if ($entry.ContainsKey('Enabled')) { [bool]$entry.Enabled } else { $true })
                VehicleScope = $(if ($entry.ContainsKey('VehicleScope')) { [string[]]@($entry.VehicleScope) } else { [string[]]@() })
                Node         = $(if ($entry.ContainsKey('Node')) { [int]$entry.Node } else { $null })
            })
    }
    if ($points.Count -eq 0) { throw "WaitingPoints in $Where is empty; give `$false for none." }
    return , $points.ToArray()
}

function New-L2DefaultWaitingPoints {
    param([Parameter(Mandatory)][int]$Count)

    for ($k = 1; $k -le $Count; $k++) {
        [ordered]@{
            StationId    = $script:FirstWaitingPointStation + $k - 1
            StationName  = "等待点$k"
            Enabled      = $true
            VehicleScope = [string[]]@()
            Node         = $null
        }
    }
}

<#
Runs the built host once with --migrate-only against this run's database, so the FieldOps import has a migrated database
to write to before the server's first start. Throws unless it exits 0 within the timeout.
#>
function Invoke-L2HostMigrateOnly {
    param(
        [Parameter(Mandatory)][string]$HostDirectory,
        [Parameter(Mandatory)][hashtable]$Environment,
        [Parameter(Mandatory)][string]$LogRoot,
        [int]$TimeoutSeconds = 120
    )

    $handle = Start-L2Process -Name 'control-server-migrate' `
        -FilePath (Join-Path $HostDirectory 'ControlServer.Host.exe') -ArgumentList @('--migrate-only') `
        -WorkingDirectory $HostDirectory -Environment $Environment -LogRoot $LogRoot
    if (-not $handle.Process.WaitForExit($TimeoutSeconds * 1000)) {
        $handle.Process.Kill($true)
        throw "ControlServer.Host --migrate-only did not exit within $TimeoutSeconds s; see $($handle.OutLog)."
    }
    $handle.Process.WaitForExit()
    if ($handle.Process.ExitCode -ne 0) {
        throw "ControlServer.Host --migrate-only exited with $($handle.Process.ExitCode); see $($handle.OutLog) and $($handle.ErrLog)."
    }
}

<#
Writes the controlled CSV and the Map's station catalog (read from the fake RIoT's control plane, which does not count as a
catalog read) into the stage, and imports them with the formal FieldOps verb. Returns the verb's JSON.
#>
function Invoke-L2WaitingPointImport {
    param(
        [Parameter(Mandatory)][object[]]$Points,
        [Parameter(Mandatory)][int]$MapId,
        [Parameter(Mandatory)][string[]]$Fleet,
        [Parameter(Mandatory)][object]$Riot,
        [Parameter(Mandatory)][scriptblock]$InvokeFieldOps,
        [Parameter(Mandatory)][string]$StageRoot
    )

    $map = @($Riot.Snapshot().body.maps | Where-Object { [int]$_.mapId -eq $MapId })
    if ($map.Count -ne 1) { throw "The fake RIoT has no Map $MapId to take the station catalog from." }
    $catalogPath = Join-Path $StageRoot "waiting-points-catalog-$MapId.json"
    [ordered]@{
        mapId    = $MapId
        stations = @($map[0].stations | ForEach-Object { [ordered]@{ stationId = [int]$_.id; stationName = [string]$_.name } })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $catalogPath -Encoding utf8NoBOM

    $csvPath = Join-Path $StageRoot 'waiting-points.csv'
    $lines = @('map_id,station_id,station_name,enabled,vehicle_scope') + @($Points | ForEach-Object {
            "$MapId,$($_.StationId),$($_.StationName),$(([string]$_.Enabled).ToLowerInvariant()),$($_.VehicleScope -join ';')"
        })
    Set-Content -LiteralPath $csvPath -Value $lines -Encoding utf8NoBOM

    return & $InvokeFieldOps -Arguments @(
        'import-waiting-points', '--input', $csvPath, '--catalog', $catalogPath,
        '--map', [string]$MapId, '--fleet', ($Fleet -join ';'))
}

Export-ModuleMember -Function Resolve-L2WaitingPoints, Invoke-L2HostMigrateOnly, Invoke-L2WaitingPointImport
