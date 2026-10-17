#Requires -Version 7
# READ-ONLY: runs one SELECT against the V2 database opened Mode=ReadOnly. Refuses anything not starting with SELECT.
param([string] $Sql, [string] $SqlFile)
if ($SqlFile) { $Sql = Get-Content $SqlFile -Raw -Encoding utf8 }
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if ($Sql.TrimStart() -notmatch '^(?i)select\s' -or $Sql.TrimEnd().TrimEnd(';') -match ';') { throw 'refusing: one SELECT only' }
$tools = 'D:\zhengyushao\control-server-v2-ops\sqlite-tools'
Get-ChildItem $tools -Filter '*.dll' | Where-Object Name -NotLike 'e_sqlite3*' | ForEach-Object { Add-Type -Path $_.FullName }
[SQLitePCL.Batteries_V2]::Init()
$c = [Microsoft.Data.Sqlite.SqliteConnection]::new('Data Source=C:\ProgramData\8005\ControlServer.V2\data\controlserver.db;Mode=ReadOnly')
$c.Open()
$cmd = $c.CreateCommand(); $cmd.CommandText = $Sql; $r = $cmd.ExecuteReader()
$names = 0..($r.FieldCount-1) | ForEach-Object { $r.GetName($_) }
while ($r.Read()) { ((0..($r.FieldCount-1) | ForEach-Object { "$($names[$_])=$(if ($r.IsDBNull($_)) { '' } else { $r.GetValue($_) })" }) -join ' | ') }
$r.Close(); $c.Close()
