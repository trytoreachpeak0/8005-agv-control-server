#Requires -Version 7
# READ-ONLY: Warning/Error/Fatal lines of the newest V2 log after -SinceUtc, excluding known-benign noise. One compact line each.
param([Parameter(Mandatory)][string] $SinceUtc, [string] $LogPath, [int] $Tail = 4000)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$f = $LogPath ? $LogPath : (Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName
$since = ([DateTimeOffset]::Parse($SinceUtc)).UtcDateTime
Get-Content $f -Tail $Tail -Encoding utf8 | Where-Object { $_ -match '"@l":"(Warning|Error|Fatal)"' -and $_ -notmatch 'ROUTE_GRAPH_DESIGN_STATE_EXPIRED' } | ForEach-Object {
  $e = $_ | ConvertFrom-Json
  $t = ([DateTimeOffset]$e.'@t').UtcDateTime
  if ($t -gt $since) {
    $msg = $e.'@mt'
    foreach ($p in $e.PSObject.Properties) { if ($p.Name -notmatch '^@|EventId|SourceContext|Scope') { $msg = $msg.Replace('{' + $p.Name + '}', "$($p.Value)") } }
    "$($t.ToString('o')) $($e.'@l') $($e.EventId.Id) $($msg.Substring(0, [Math]::Min(260, $msg.Length)))"
  }
}
