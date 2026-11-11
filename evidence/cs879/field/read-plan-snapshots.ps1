#Requires -Version 7
# READ-ONLY (cs#879): every UpcomingStopPlanSnapshot the v2 server sent for journey 8f62304a, 2026-11-04 20:15-21:00 UTC.
# Ran on factory01 against the v2 database (Mode=ReadOnly); output in plan-snapshots-20261104T2018-2053Z.txt.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ToolDir = 'D:\zhengyushao\control-server-v2-ops\sqlite-tools'
foreach ($a in 'SQLitePCLRaw.core.dll','SQLitePCLRaw.provider.e_sqlite3.dll','SQLitePCLRaw.batteries_v2.dll','Microsoft.Data.Sqlite.dll') { Add-Type -LiteralPath (Join-Path $ToolDir $a) -ErrorAction SilentlyContinue }
try { [SQLitePCL.Batteries_V2]::Init() } catch { }
$c = [Microsoft.Data.Sqlite.SqliteConnection]::new('Data Source=C:\ProgramData\8005\ControlServer.V2\data\controlserver.db;Mode=ReadOnly;Pooling=False')
$c.Open()
$cmd = $c.CreateCommand()
$cmd.CommandText = "SELECT CreatedAt, MessageType, PayloadJson FROM ProtocolOutbox WHERE PayloadJson LIKE '%8f62304a-7505-49df-aaf4-1c840a9407b3%' AND MessageType = 'UpcomingStopPlanSnapshot' AND CreatedAt >= '2026-11-04 20:15' AND CreatedAt < '2026-11-04 21:00' ORDER BY CreatedAt"
$r = $cmd.ExecuteReader()
while ($r.Read()) {
  $p = ($r.GetString(2) | ConvertFrom-Json).payload
  $legs = ($p.legs | ForEach-Object { "$($_.sequence):$($_.stationId)/$($_.state)" }) -join ' '
  "$($r.GetString(0)) rev=$($p.planRevision) $legs"
}
$r.Close(); $c.Close()
