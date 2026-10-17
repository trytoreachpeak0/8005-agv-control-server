#Requires -Version 7
# READ-ONLY: GET /api/runtime/sessions on the V2 instance.
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Invoke-RestMethod 'http://172.19.205.222:58107/api/runtime/sessions' -NoProxy -TimeoutSec 10 | ConvertTo-Json -Depth 6
