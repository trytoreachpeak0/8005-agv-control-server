#Requires -Version 7
# control-server#639: read-only check of mode and breakSwitchState on agv02/agv03 before the predicate change.
# GET only. Never agv01. The call key is read from the environment and never printed.
$ErrorActionPreference = 'Stop'
$baseUrl = 'http://172.19.206.222:8888'
$vehicles = [ordered]@{
    agv02 = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4'
    agv03 = 'BROKERX-7daca4ee91da498d8026c68b7b941127'
}
$route = (Find-NetRoute -RemoteIPAddress 172.19.206.222 | Select-Object -First 1).InterfaceAlias
"route interface: $route"
if ($route -match 'Clash') { throw 'Route goes through Clash TUN; result would be fiction.' }
$key = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine') ??
    [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'User')
if ([string]::IsNullOrEmpty($key)) { throw 'CONTROL_SERVER_RIOT_CALL_API_KEY not set.' }
$headers = @{ Authorization = "Bearer $key" }
foreach ($name in $vehicles.Keys) {
    $deviceKey = $vehicles[$name]
    $readAt = Get-Date -Format 'yyyy-MM-ddTHH:mm:ss.fffzzz'
    $response = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/task/v1/task/getVehicleInfo/$deviceKey" -Headers $headers -TimeoutSec 15
    $v = $response.vehicle
    $t = $response.vehicleTaskInfo
    [ordered]@{
        car              = $name
        readAt           = $readAt
        vehicleKey       = $v.key
        taskInfoKey      = $t.key
        mode             = $v.mode
        breakSwitchState = $v.breakSwitchState
        movementState    = $v.movementState
        moveTaskNo       = $v.moveTaskNo
        speed            = $v.speed
        controlState     = $v.controlState
        emergencyState   = $v.emergencyState
        locationState    = $v.locationState
        procState        = $t.procState
        processingOrder  = $t.processingOrder
        enable           = $t.enable
        integrationLevel = $t.integrationLevel
    } | ConvertTo-Json -Compress
}
