#Requires -Version 7
<#
Compares the cs804-preseed-db.json dumps (every table at "Environment is up") of two L2 rounds, scenario by scenario.
Values that differ from run to run by construction are normalised first: GUIDs, ISO-8601 instants, 64-hex digests
(content hashes over rows that carry instants), and the stage path. Prints, per scenario, every table whose row
count or normalised rows differ, with the first differing row of each side.
#>
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    [string]$Report
)

function Normalize([string]$text) {
    $text = $text -replace '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', '<guid>'
    $text = $text -replace '\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?', '<time>'
    $text = $text -replace '\b[0-9a-f]{64}\b', '<sha256>'
    $text = $text -replace '\b[0-9a-f]{32}\b', '<hex32>'
    $text = $text -replace 'l2-\d{8}T\d{9}Z-slot\d', '<stage>'
    $text = $text -replace '\d{8}T\d{9}Z', '<runid>'
    $text = $text -replace '"(\w*Ticks)":-?\d+', '"$1":<ticks>'
    $text = $text -replace 'cs804\\\\l2-(before|after)\\\\', 'cs804\\<root>\\'
    return $text
}

function Read-Dump([string]$path) {
    $dump = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
    $tables = [ordered]@{}
    foreach ($name in $dump.Keys) {
        $tables[$name] = @($dump[$name] | ForEach-Object { Normalize (ConvertTo-Json -InputObject $_ -Compress -Depth 6) } | Sort-Object)
    }
    $tables
}

# Written by the running server and the peers' traffic, not by the preseed: how many rows sit there at the moment of the
# dump depends on timing. Compared and reported apart, never counted as a preseed difference.
$runtimeTables = @('ProtocolInbox', 'ProtocolOutbox', 'SessionRecoveries', 'OnboardAlarmSnapshots', 'VehicleSnapshotRevisions',
    'VehicleDispatchBudgets', 'MapStationCatalogStates', 'RouteGraphSnapshots', 'RouteGraphStations', 'RouteGraphEdges',
    'RouteGraphEdgeGroups', 'ConnectionRecoveries', 'VehicleRecoveryGenerations')
$runtimeDiffers = @{}
$lines = [Collections.Generic.List[string]]::new()
$same = 0; $differ = 0; $missing = 0
foreach ($dir in Get-ChildItem -LiteralPath $Before -Directory | Where-Object Name -ne '_stage' | Sort-Object Name) {
    $b = Join-Path $dir.FullName 'snapshots/cs804-preseed-db.json'
    $a = Join-Path $After "$($dir.Name)/snapshots/cs804-preseed-db.json"
    if (-not (Test-Path $b) -or -not (Test-Path $a)) {
        $missing++; $lines.Add("$($dir.Name): no dump (before $(Test-Path $b), after $(Test-Path $a))"); continue
    }
    $bt = Read-Dump $b; $at = Read-Dump $a
    $diffs = @(foreach ($name in @(@($bt.Keys) + @($at.Keys) | Sort-Object -Unique)) {
            $br = @($bt[$name]); $ar = @($at[$name])
            if (($br -join "`n") -cne ($ar -join "`n") -and $name -in $runtimeTables) {
                $runtimeDiffers[$name] = 1 + ($runtimeDiffers[$name] ?? 0)
            } elseif (($br -join "`n") -cne ($ar -join "`n")) {
                $onlyB = @($br | Where-Object { $_ -cnotin $ar }); $onlyA = @($ar | Where-Object { $_ -cnotin $br })
                "  $name rows $($br.Count) -> $($ar.Count)`n    before-only: $($onlyB | Select-Object -First 1)`n    after-only:  $($onlyA | Select-Object -First 1)"
            }
        })
    if ($diffs.Count -eq 0) { $same++ } else { $differ++; $lines.Add("$($dir.Name): $($diffs.Count) table(s) differ"); $diffs | ForEach-Object { $lines.Add($_) } }
}
$lines.Insert(0, "identical $same, differing $differ, missing $missing (preseed tables; runtime tables apart)")
$lines.Insert(1, "runtime tables that differed (scenarios): " + (($runtimeDiffers.Keys | Sort-Object | ForEach-Object { "$_=$($runtimeDiffers[$_])" }) -join ', '))
if ($Report) { $lines | Set-Content -LiteralPath $Report -Encoding utf8NoBOM }
$lines
