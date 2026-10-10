#Requires -Version 7
<#
    control-server#571 mutation check. Each mutation breaks one guard in the committed implementation, runs the
    self-test (or the C# test class), and asserts that the named cases go red. The file is restored from HEAD
    after every mutation, so the worktree must be clean before this starts.
#>
param(
    [Parameter(Mandatory = $true)][string] $Worktree,
    [Parameter(Mandatory = $true)][string] $OutDir,
    [string] $Only
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $Worktree
if (git status --porcelain) { throw 'worktree not clean; commit before mutating' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$psm = 'scripts/parallel/ParallelInstance.psm1'
$evt = 'src/ControlServer.Host/Runtime/EffectiveConfigurationEvent.cs'
$mutations = @(
    @{ Id = 'M01'; File = $psm; Runner = 'self'; What = 'agv01 scan never refuses'
        Old = "`n    if (`$hits.Count -gt 0) {"; New = "`n    if (`$false -and `$hits.Count -gt 0) {"
        Expect = @('fleet: agv01 in place of the second row', 'fleet: agv01 appended as a third row', 'fleet: Assert- throws on agv01', 'journeyRuntime.fleet roster with agv01 mixed in') }
    @{ Id = 'M02'; File = $psm; Runner = 'self'; What = 'riotId 58 not treated as agv01'
        Old = 'if ((ConvertTo-IntegerOrNull $row[$key]) -eq $script:ProductionVehicle.RiotId -or "$($row[$key])".Trim() -eq "$($script:ProductionVehicle.RiotId)") {'
        New = 'if ($false) {'
        Expect = @('fleet: only the RIoT id of row 0 is agv01''s (58)') }
    @{ Id = 'M03'; File = $psm; Runner = 'self'; What = 'agv01 string compare made exact (ordinal, case-sensitive)'
        Old = 'if ($leaf.Value -eq $script:ProductionVehicle.AgvId -or $leaf.Value -eq $script:ProductionVehicle.VehicleKey) {'
        New = 'if ($leaf.Value -ceq $script:ProductionVehicle.AgvId -or $leaf.Value -ceq $script:ProductionVehicle.VehicleKey) {'
        Expect = @('fleet: agv01''s vehicleKey in lower case is still refused as agv01') }
    @{ Id = 'M04'; File = $psm; Runner = 'self'; What = 'riotId not compared with the registry'
        Old = '        if ($riotId -ne $match.RiotId) {'; New = '        if ($false) {'
        Expect = @('fleet: row 0''s RIoT id is agv03''s (60)') }
    @{ Id = 'M05'; File = $psm; Runner = 'self'; What = 'deviceKey not compared with vehicleKey'
        Old = '        if (-not [string]::Equals($deviceKey, $vehicleKey, [StringComparison]::Ordinal)) {'; New = '        if ($false) {'
        Expect = @('fleet: row 0''s deviceKey is agv03''s') }
    @{ Id = 'M06'; File = $psm; Runner = 'self'; What = 'agvId not compared with the registry'
        Old = '        if (-not [string]::Equals([string] $row[''agvId''], $match.AgvId, [StringComparison]::Ordinal)) {'; New = '        if ($false) {'
        Expect = @('fleet: row 1''s agvId is agv02''s name on agv03''s key') }
    @{ Id = 'M07'; File = $psm; Runner = 'self'; What = 'registry lookup case-insensitive'
        Old = '        $match = $script:AllowedVehicles | Where-Object { [string]::Equals($_.VehicleKey, $vehicleKey, [StringComparison]::Ordinal) } | Select-Object -First 1'
        New = '        $match = $script:AllowedVehicles | Where-Object { [string]::Equals($_.VehicleKey, $vehicleKey, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1'
        Expect = @('fleet: row 0''s vehicleKey and deviceKey in lower case') }
    @{ Id = 'M08'; File = $psm; Runner = 'self'; What = 'roster beside single keys tolerated'
        Old = '    if ($single.Count -gt 0) {'; New = '    if ($false) {'
        Expect = @('fleet: roster beside journeyRuntime.agvId/vehicleKey', 'fleet: roster beside journeyRuntime.agvLifecycleGeneration') }
    @{ Id = 'M09'; File = $psm; Runner = 'self'; What = 'same car twice tolerated'
        Old = '        if ($seen.ContainsKey($match.Alias)) {'; New = '        if ($false) {'
        Expect = @('fleet: the same car twice') }
    @{ Id = 'M10'; File = $psm; Runner = 'self'; What = 'riotId not required'
        Old = "        foreach (`$key in @('riotId', 'agvLifecycleGeneration')) {"; New = "        foreach (`$key in @('agvLifecycleGeneration')) {"
        Expect = @('fleet: row 1 without riotId') }
    @{ Id = 'M11'; File = $psm; Runner = 'self'; What = 'task types not held to allowedWorkTypes'
        Old = '                @{ Key = ''allowedTaskTypes''; Scope = $allowedWorkTypes'; New = '                @{ Key = ''allowedTaskTypes''; Scope = $null'
        Expect = @('fleet: a row task type outside allowedWorkTypes', 'fleet: production MesIngest source, a row allowing WIRE_TO_GATE') }
    @{ Id = 'M12'; File = $psm; Runner = 'self'; What = 'overlay writes deployment-only row keys'
        Old = '                foreach ($key in $script:FleetRowProductKeys) {'; New = '                foreach ($key in $script:FleetRowKeys) {'
        Expect = @('fleet overlay: product keys only') }
    @{ Id = 'M13'; File = $psm; Runner = 'self'; What = 'overlay does not write the primary pair'
        Old = '        $journeyOverlay[''vehicleKey''] = $rows[0][''vehicleKey'']'; New = ''
        Expect = @('fleet overlay: the primary pair the Host requires is the first row') }
    @{ Id = 'M14'; File = $psm; Runner = 'self'; What = 'single-car overlay leaves a stale roster'
        Old = "    } else {`n        `$journeyOverlay['fleet'] = @()`n    }"; New = '    }'
        Expect = @('single-car overlay: writes an empty roster') }
    @{ Id = 'M15'; File = $psm; Runner = 'self'; What = 'missing car not reported'
        Old = '        if ($sameKey.Count -eq 0) {'; New = '        if ($sameKey.Count -eq 0) { continue'
        Expect = @('fleet read-back: one car fewer than defined') }
    @{ Id = 'M16'; File = $psm; Runner = 'self'; What = 'extra car not reported'
        Old = '            $problems += "fleet: $($car.VehicleKey) (''$($car.AgvId)'') is bound, the definition does not name it"'; New = ''
        Expect = @('fleet read-back: one car more than defined (agv01)', 'single-car read-back: the Host bound a roster the definition does not have') }
    @{ Id = 'M17'; File = $psm; Runner = 'self'; What = 'per-car lists not compared'
        Old = '            if ((@($pair.Expected) -join "`n") -cne (@($pair.Actual) -join "`n")) {' + "`n" + '                $problems += "fleet:'
        New = '            if ($false) {' + "`n" + '                $problems += "fleet:'
        Expect = @('fleet read-back: a car bound with different task types', 'fleet read-back: a car bound with different zones') }
    @{ Id = 'M18'; File = $psm; Runner = 'self'; What = 'per-car agvId not compared'
        Old = '        if (-not [string]::Equals($car.AgvId, [string] $row[''agvId''], [StringComparison]::Ordinal)) {'; New = '        if ($false) {'
        Expect = @('fleet read-back: a car bound under a different agvId') }
    @{ Id = 'M19'; File = $psm; Runner = 'self'; What = 'roster definition accepts an event without a roster'
        Old = '    if ($definesFleet -and -not $hasFleet) {'; New = '    if ($false) {'
        Expect = @('fleet read-back: a Host that does not log the roster') }
    @{ Id = 'M20'; File = $psm; Runner = 'self'; What = 'no event at all only warns for a roster definition'
        Old = "            -not `$Definition['journeyRuntime'].ContainsKey('fleet')) { 'Warn' }"; New = "            `$true) { 'Warn' }"
        Expect = @('fleet read-back: no EFFECTIVE_CONFIGURATION event at all, fake source') }
    @{ Id = 'M21'; File = $evt; Runner = 'cs'; What = 'event logs a fifth field per car'
        Old = '            ["Zones"] = vehicle.Zones,'; New = '            ["Zones"] = vehicle.Zones,' + "`n" + '            ["AgvLifecycleGeneration"] = vehicle.AgvLifecycleGeneration,'
        Expect = @('EffectiveConfigurationEventCarriesEachRosterVehicleWithExactlyFourFields') }
    @{ Id = 'M22'; File = $evt; Runner = 'cs'; What = 'event logs an empty roster always'
        Old = '        .. effective.Fleet.Select('; New = '        .. effective.Fleet.Take(0).Select('
        Expect = @('EffectiveConfigurationEventCarriesEachRosterVehicleWithExactlyFourFields', 'InstallerReadBackJudgesTheEventTheHostReallyLogs') }
    @{ Id = 'M23'; File = $psm; Runner = 'cs'; What = 'overlay does not write the primary pair (C# binding)'
        Old = '        $journeyOverlay[''vehicleKey''] = $rows[0][''vehicleKey'']'; New = ''
        Expect = @('TwoCarExampleOverPackageAppSettingsBindsBothRowsWholeWithAgv02Primary') }
    @{ Id = 'M24'; File = $psm; Runner = 'cs'; What = 'missing car not reported (C# end to end)'
        Old = '        if ($sameKey.Count -eq 0) {'; New = '        if ($sameKey.Count -eq 0) { continue'
        Expect = @('InstallerReadBackJudgesTheEventTheHostReallyLogs') }
    @{ Id = 'M25'; File = $psm; Runner = 'cs'; What = 'agv01 scan never refuses and registry lookup accepts agv01 (C#)'
        Old = "`n    if (`$hits.Count -gt 0) {"; New = "`n    if (`$false -and `$hits.Count -gt 0) {"
        Also = @{ Old = '$script:AllowedVehicles = @('; New = '$script:AllowedVehicles = @(' + "`n" + '    [pscustomobject]@{ Alias = ''agv01''; AgvId = ''老厂前线新多仓位1''; VehicleKey = ''BROKERX-0c20ff0600d644869a6a80c186065d85''; RiotId = 58 }' }
        Expect = @('InstallerModuleRefusesTheTwoCarExampleWithAgv01InARow') }
)

$results = @()
foreach ($m in $mutations) {
    if ($Only -and $m.Id -notin ($Only -split ",")) { continue }
    $path = Join-Path $Worktree $m.File
    $text = [IO.File]::ReadAllText($path)
    $edits = @(@{ Old = $m.Old; New = $m.New }) + @($m.Also | Where-Object { $_ })
    $landed = $true
    foreach ($e in $edits) {
        $count = ([regex]::Matches($text, [regex]::Escape($e.Old))).Count
        if ($count -ne 1) { $landed = $false; Write-Host "$($m.Id): pattern found $count times: $($e.Old)" -ForegroundColor Red; break }
        $text = $text.Replace($e.Old, $e.New)
    }
    if (-not $landed) { $results += [pscustomobject]@{ Id = $m.Id; What = $m.What; Killed = $false; Note = 'mutation did not land' }; continue }
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    $log = Join-Path $OutDir "$($m.Id).log"
    try {
        if ($m.Runner -eq 'self') {
            & pwsh -NoProfile -File scripts/parallel/Test-ParallelInstance.ps1 *> $log
            $failed = @(Get-Content -LiteralPath $log | Where-Object { $_ -match '^\s+FAIL\s+(.*)$' } | ForEach-Object { $Matches[1] })
        } else {
            & dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --filter 'FullyQualifiedName~ParallelInstanceEffectiveConfigurationTests' *> $log
            $logLines = @(Get-Content -LiteralPath $log)
            if (@($logLines | Where-Object { $_ -cmatch ': error [A-Z]+[0-9]+' }).Count -gt 0) {
                $failed = @('BUILD FAILED')
            } else {
                $failed = @($logLines | Where-Object { $_.TrimStart().StartsWith('Failed ControlServer.Tests.') })
            }
        }
    } finally {
        git checkout HEAD -- $m.File
    }
    $missing = @($m.Expect | Where-Object { $name = $_; -not ($failed | Where-Object { $_.Contains($name) }) })
    $results += [pscustomobject]@{ Id = $m.Id; What = $m.What; Killed = ($missing.Count -eq 0 -and $failed -notcontains 'BUILD FAILED')
        Note = "red: $($failed.Count); expected but green: $($missing -join ' / ')" }
    Write-Host ("{0} {1,-6} {2}" -f $m.Id, ($results[-1].Killed ? 'KILLED' : 'SURVIVED'), $m.What)
}
if (git status --porcelain) { throw 'worktree not clean after mutations' }
$results | Format-Table -AutoSize | Out-String -Width 400 | Set-Content -LiteralPath (Join-Path $OutDir 'summary.txt')
$results | Format-Table -AutoSize | Out-String -Width 400
