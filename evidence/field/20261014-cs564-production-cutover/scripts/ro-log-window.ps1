#Requires -Version 7
# READ-ONLY: lines of a V2 log (newest unless -LogPath) in a UTC window whose text matches -Pattern; compact one line each.
param(
  [Parameter(Mandatory)][string] $FromUtc,
  [Parameter(Mandatory)][string] $ToUtc,
  [string] $Pattern = '.',
  [string] $Exclude = 'Heartbeat|ROUTE_GRAPH_DESIGN_STATE_EXPIRED|vehicle-safety|HTTP GET',
  [string] $LogPath)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$f = $LogPath ? $LogPath : (Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName
$a = ([DateTimeOffset]::Parse($FromUtc)).UtcDateTime; $b = ([DateTimeOffset]::Parse($ToUtc)).UtcDateTime
Get-Content $f -Encoding utf8 | Where-Object { $_ -match $Pattern -and $_ -notmatch $Exclude } | ForEach-Object {
  $e = $_ | ConvertFrom-Json; $t = ([DateTimeOffset]$e.'@t').UtcDateTime
  if ($t -ge $a -and $t -le $b) {
    $msg = $e.'@mt'; foreach ($p in $e.PSObject.Properties) { if ($p.Name -notmatch '^@|EventId|SourceContext|Scope') { $msg = $msg.Replace('{' + $p.Name + '}', "$($p.Value)") } }
    "$($t.ToString('HH:mm:ss.fff')) $(($e.'@l' ?? 'Info').Substring(0,4)) $($e.EventId.Id)/$($e.EventId.Name) $($msg.Substring(0,[Math]::Min(330,$msg.Length)))"
  }
}
