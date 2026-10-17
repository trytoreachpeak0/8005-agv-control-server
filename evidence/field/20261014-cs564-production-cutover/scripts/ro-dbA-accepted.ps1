#Requires -Version 7
# READ-ONLY: accepted demands in database A (trial) with their intents' upper ids; prints sublot|workType per line.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$tools = 'D:\zhengyushao\control-server-v2-ops\sqlite-tools'
Get-ChildItem $tools -Filter '*.dll' | Where-Object Name -NotLike 'e_sqlite3*' | ForEach-Object { Add-Type -Path $_.FullName }
[SQLitePCL.Batteries_V2]::Init()
$f = 'C:\ProgramData\8005\ControlServer.V2.pre-cutover-production-20261014\data\controlserver.db'
$c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$f;Mode=ReadOnly"); $c.Open()
$cmd = $c.CreateCommand(); $cmd.CommandText = "SELECT a.Sublot, a.WorkType, a.Status, a.DemandId, (SELECT group_concat(UpperId || '=' || IFNULL(OrderId,''), ' ; ') FROM OrderIntents i WHERE i.DemandId = a.DemandId) FROM AcceptedDemands a ORDER BY a.AcceptedAt"
$r = $cmd.ExecuteReader(); while ($r.Read()) { "$($r.GetValue(0))`t$($r.GetValue(1))`t$($r.GetValue(2))`t$($r.GetValue(3))`t$(if ($r.IsDBNull(4)) { '' } else { $r.GetValue(4) })" }; $r.Close(); $c.Close()
