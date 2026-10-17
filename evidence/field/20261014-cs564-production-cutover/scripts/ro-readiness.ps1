#Requires -Version 7
# READ-ONLY: SessionReadiness events of today's V2 log, one line each: time, vehicle, generation, verdict, reason.
param([int] $Last = 20)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Get-Content ((Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName) -Encoding utf8 | Where-Object { $_ -match 'SessionReadiness' } | Select-Object -Last $Last | ForEach-Object {
  $e = $_ | ConvertFrom-Json
  "$(([datetime]$e.'@t').ToLocalTime().ToString('HH:mm:ss')) $($e.AgvId) gen=$($e.SessionGeneration) $($e.Readiness) [$($e.ReasonCode)] after $($e.AnsweredMessageType)"
}
