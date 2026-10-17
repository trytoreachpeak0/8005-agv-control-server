#Requires -Version 7
# READ-ONLY: GET /api/order/v1/orderRecord/detailByOrderId/{id} for each id, with the server's CallApiKey. No writes.
param([Parameter(Mandatory)][string[]] $OrderId)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$key = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine') ?? [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY')
if (-not $key) { throw 'key not set' }
foreach ($id in $OrderId) {
  $r = Invoke-WebRequest -Method Get -Uri "http://172.19.206.222:8888/api/order/v1/orderRecord/detailByOrderId/$id" -Headers @{ Authorization = "Bearer $key" } -TimeoutSec 20 -SkipHttpErrorCheck -NoProxy
  "== $id status=$($r.StatusCode)"
  $r.Content
}
