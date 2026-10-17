#Requires -Version 7
# READ-ONLY after the user's manual RIoT moves: holds, foreign orders, purpose claims, intents, foreign-order warnings.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir = 'D:\zhengyushao\control-server-v2-ops\prod-20261014'
"### now $(Get-Date -Format o)"
foreach ($t in 'ForeignRiotOrders','ManualChargingHolds','VehiclePurposeClaims','ChargingCycles','VehicleFaultStates','StationExclusivities') {
  "### $t"; try { & "$dir\ro-sql.ps1" -Sql "SELECT * FROM $t" } catch { "  ERR $_" }
}
"### OrderIntents not CONFIRMED or since 12:30Z"; & "$dir\ro-sql.ps1" -Sql "SELECT UpperId, Purpose, Status, OrderId, CreatedAt FROM OrderIntents WHERE Status <> 'CONFIRMED' OR CreatedAt > '2026-10-14 12:30'"
"### StructuralDispatchBlocks open"; & "$dir\ro-sql.ps1" -Sql "SELECT * FROM StructuralDispatchBlocks WHERE ClearedAt IS NULL"
"### log: foreign / hold / external-order lines since 12:38Z"
Get-Content ((Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName) -Encoding utf8 | Where-Object { $_ -match '(?i)foreign|external order|ManualCharging|hold' } | Where-Object { [DateTime]::Parse((($_ | ConvertFrom-Json).'@t')).ToUniversalTime() -gt [DateTime]'2026-10-14T12:38:00' } | Select-Object -Last 15 | ForEach-Object { $_.Substring(0,[Math]::Min(450,$_.Length)) }
"### warn/err since 12:38Z"
Get-Content ((Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName) -Encoding utf8 | Where-Object { $_ -match '"@l":"(Warning|Error|Fatal)"' } | Where-Object { [DateTime]::Parse((($_ | ConvertFrom-Json).'@t')).ToUniversalTime() -gt [DateTime]'2026-10-14T12:38:00' } | Select-Object -Last 15 | ForEach-Object { $_.Substring(0,[Math]::Min(450,$_.Length)) }
