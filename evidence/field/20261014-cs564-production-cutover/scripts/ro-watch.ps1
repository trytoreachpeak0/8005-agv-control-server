#Requires -Version 7
# READ-ONLY S4 watch: SELECT only (Mode=ReadOnly, DLLs from sqlite-tools copy), FieldOps read verbs only,
# Get-Service, reading files, HTTP GET to RIoT. No import, no write verb, no service control.
param([string] $SinceUtc = '2026-10-14 00:00', [switch] $Logs)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$exe = 'D:\zhengyushao\control-server-v2-ops\fieldops-96a88db0\ControlServer.FieldOps.exe'
$db  = 'C:\ProgramData\8005\ControlServer.V2\data\controlserver.db'
$tools = 'D:\zhengyushao\control-server-v2-ops\sqlite-tools'
Get-ChildItem $tools -Filter '*.dll' | Where-Object Name -NotLike 'e_sqlite3*' | ForEach-Object { Add-Type -Path $_.FullName }
[SQLitePCL.Batteries_V2]::Init()
$c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Mode=ReadOnly")
$c.Open()
function Q([string]$sql) {
  $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $r = $cmd.ExecuteReader()
  $names = 0..($r.FieldCount-1) | ForEach-Object { $r.GetName($_) }
  while ($r.Read()) { '  ' + ((0..($r.FieldCount-1) | ForEach-Object { "$($names[$_])=$(if ($r.IsDBNull($_)) { '' } else { $r.GetValue($_) })" }) -join ' | ') }
  $r.Close()
}
"### now $(Get-Date -Format o)"
"### services"
Get-Service '8005 AGV ControlServer','8005 AGV ControlServer V2' | ForEach-Object { "  $($_.Name) $($_.Status)/$($_.StartType)" }
$cfg = Get-Content 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
"### gate RiotCreateDispatch.enabled=$($cfg.RiotCreateDispatch.enabled)"
"### area-assignments (read verb)"; (& $exe area-assignments --database $db) | ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0,400) + '...' } else { $_ } }
"### dispatch-zone-parameters (read verb)"; & $exe dispatch-zone-parameters --database $db
"### journeys not Completed"
Q "SELECT substr(JourneyId,1,40) AS j, AgvId, Stage, DispatchZone, LoadingPhaseState, LoadingClosedReason, CargoHoldingStartedAt, ExpectedBasketCount, BlockReasonCode FROM JourneyRuntimes WHERE Stage <> 'Completed'"
"### journey count by stage"; Q "SELECT Stage, COUNT(*) n FROM JourneyRuntimes GROUP BY Stage"
"### accepted not Succeeded"
Q "SELECT substr(DemandId,1,8) d, Sublot, WorkType, Status, AcceptedAt, json_extract(LiveMesFieldsJson,'$.area') area FROM AcceptedDemands WHERE Status <> 'Succeeded'"
"### accepted total"; Q "SELECT COUNT(*) n, MAX(AcceptedAt) last FROM AcceptedDemands"
"### journey demands (open journeys)"
try { Q "SELECT * FROM JourneyDemands WHERE JourneyId IN (SELECT JourneyId FROM JourneyRuntimes WHERE Stage <> 'Completed')" } catch { "  ERR $_" }
"### stops of open journeys"
Q "SELECT substr(JourneyId,1,8) j, Sequence, StopRole, StationId, StationRiotId, UpperId, Status FROM JourneyStops WHERE JourneyId IN (SELECT JourneyId FROM JourneyRuntimes WHERE Stage <> 'Completed') ORDER BY JourneyId, Sequence"
"### intents created since $SinceUtc or not CONFIRMED"
Q "SELECT UpperId, Purpose, TargetStationId, Status, OrderId, CreateAttemptCount, CreatedAt FROM OrderIntents WHERE CreatedAt > '$SinceUtc' OR Status NOT IN ('CONFIRMED') ORDER BY CreatedAt"
"### charging/purpose/holds"
Q "SELECT CycleId, VehicleKey, Phase FROM ChargingCycles WHERE Phase <> 'ENDED'"
Q "SELECT * FROM VehiclePurposeClaims"
Q "SELECT * FROM ManualChargingHolds"
$c.Close()
"### riot"
$k = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY','User') ?? [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY','Machine')
$cars = [ordered]@{ agv01='BROKERX-0c20ff0600d644869a6a80c186065d85'; agv02='BROKERX-f38975561adf46ccb1d2f23833c7d0e4'; agv03='BROKERX-7daca4ee91da498d8026c68b7b941127' }
foreach ($n in $cars.Keys) {
  try {
    $v = (Invoke-RestMethod "http://172.19.206.222:8888/api/task/vehicles/getVehicleInfoByDeviceKey?key=$($cars[$n])" -Headers @{ Authorization = "Bearer $k" } -TimeoutSec 10 -NoProxy).result
    if ($v -is [array]) { $v = $v[0] }
    "  $n st=$($v.currentPosition) proc=$($v.procState) move=$($v.movementState) emerg=$($v.emergencyState) bat=$($v.battery) batState=$($v.batteryState) map=$($v.currentMap) status=$($v.status)"
  } catch { "  $n riot read failed: $($_.Exception.Message)" }
}
if ($Logs) {
  "### warn/err since $SinceUtc (UTC), last 40"
  $since = [DateTime]::Parse($SinceUtc)
  Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-2026101*.ndjson' | Sort-Object Name | Select-Object -Last 2 | ForEach-Object {
    Get-Content $_.FullName -Encoding utf8 | Where-Object { $_ -match '"@l":"(Warning|Error)"' } | Where-Object { $t = ($_ | ConvertFrom-Json).'@t'; [DateTime]::Parse($t).ToUniversalTime() -gt $since }
  } | Select-Object -Last 40 | ForEach-Object { $_.Substring(0, [Math]::Min(500, $_.Length)) }
}
