#Requires -Version 7
# READ-ONLY: RIoT vehicle info for the three cars, battery-related raw fields and any timestamps. GET only.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'
$k = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY','User') ?? [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY','Machine')
$cars = [ordered]@{ agv01='BROKERX-0c20ff0600d644869a6a80c186065d85'; agv02='BROKERX-f38975561adf46ccb1d2f23833c7d0e4'; agv03='BROKERX-7daca4ee91da498d8026c68b7b941127' }
"now $(Get-Date -Format o)"
foreach ($n in $cars.Keys) {
  $v = (Invoke-RestMethod "http://172.19.206.222:8888/api/task/vehicles/getVehicleInfoByDeviceKey?key=$($cars[$n])" -Headers @{ Authorization = "Bearer $k" } -TimeoutSec 10 -NoProxy).result
  if ($v -is [array]) { $v = $v[0] }
  $props = $v.PSObject.Properties | Where-Object { $_.Name -match '(?i)batt|power|volt|curr|charg|time|update|report|online|status' }
  "== $n " + (($props | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' ')
}
