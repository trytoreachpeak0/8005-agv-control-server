#Requires -Version 7
# READ-ONLY: GET the V2 dashboard dispatch backlog; list reason codes and count SLOT_CONFIGURATION_NOT_CONFIRMED.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'
$raw = (Invoke-WebRequest 'http://172.19.205.222:58107/api/dashboard/dispatch-backlog' -NoProxy -TimeoutSec 15).Content
"now $(Get-Date -Format o) bytes=$($raw.Length)"
"SLOT_CONFIGURATION_NOT_CONFIRMED occurrences=$(([regex]::Matches($raw, 'SLOT_CONFIGURATION_NOT_CONFIRMED')).Count)"
"reason codes: $((([regex]::Matches($raw, '\b[A-Z][A-Z0-9]+(?:_[A-Z0-9]+)+\b') | ForEach-Object Value | Group-Object | ForEach-Object { "$($_.Name)x$($_.Count)" }) -join ' '))"
$raw.Substring(0, [Math]::Min(1500, $raw.Length))
