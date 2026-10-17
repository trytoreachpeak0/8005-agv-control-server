#Requires -Version 7
# READ-ONLY compact trip line: per open journey stage/block, per car RIoT position/state, latest readiness per car, V2 service.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'
$dir = 'D:\zhengyushao\control-server-v2-ops\prod-20261014'
$svc = Get-Service '8005 AGV ControlServer V2'
$j = & "$dir\ro-sql.ps1" -Sql "SELECT substr(JourneyId,9,8) j, AgvId, Stage, LoadingPhaseState, BlockReasonCode FROM JourneyRuntimes WHERE Stage <> 'Completed'"
$s = & "$dir\ro-sql.ps1" -Sql "SELECT substr(JourneyId,9,8) j, Sequence, StationRiotId, Status FROM JourneyStops WHERE JourneyId IN (SELECT JourneyId FROM JourneyRuntimes WHERE Stage <> 'Completed') ORDER BY JourneyId, Sequence"
$k = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY','User') ?? [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY','Machine')
$cars = [ordered]@{ agv01='BROKERX-0c20ff0600d644869a6a80c186065d85'; agv02='BROKERX-f38975561adf46ccb1d2f23833c7d0e4'; agv03='BROKERX-7daca4ee91da498d8026c68b7b941127' }
$r = foreach ($n in $cars.Keys) { try { $v = (Invoke-RestMethod "http://172.19.206.222:8888/api/task/vehicles/getVehicleInfoByDeviceKey?key=$($cars[$n])" -Headers @{ Authorization = "Bearer $k" } -TimeoutSec 8 -NoProxy).result; if ($v -is [array]) { $v = $v[0] }; "$n@$($v.currentPosition)/$($v.procState)/$($v.movementState)/$($v.emergencyState)/b$($v.battery)" } catch { "$n riot-err" } }
$rd = Get-Content ((Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName) -Tail 3000 -Encoding utf8 | Where-Object { $_ -match 'SessionReadiness' } | ForEach-Object { $_ | ConvertFrom-Json } | Group-Object AgvId | ForEach-Object { $e = $_.Group[-1]; "$($e.AgvId.Substring($e.AgvId.Length-1))=$($e.Readiness)[$($e.ReasonCode)]@$(([datetime]$e.'@t').ToLocalTime().ToString('HH:mm:ss'))" }
$we = @(Get-Content ((Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName) -Tail 3000 -Encoding utf8 | Where-Object { $_ -match '"@l":"(Warning|Error|Fatal)"' }).Count
"$(Get-Date -Format HH:mm:ss) svc=$($svc.Status) | $($r -join ' ') | ready: $($rd -join ' ') | warnErrTail3000=$we"
$j | ForEach-Object { "   J $_" }
$s | ForEach-Object { "   S $_" }
