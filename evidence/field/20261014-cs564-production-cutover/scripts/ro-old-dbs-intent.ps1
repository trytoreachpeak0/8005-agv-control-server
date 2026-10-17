#Requires -Version 7
# READ-ONLY: look for one RIoT order / upper id in every V2 database (current and the set-aside ones). Mode=ReadOnly,
# DLLs from the sqlite-tools copy, never from the install directory.
param([string] $UpperIdPrefix = 'W2G-30dbba5f', [string] $OrderId = 'order-2109981807503081472')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$tools = 'D:\zhengyushao\control-server-v2-ops\sqlite-tools'
Get-ChildItem $tools -Filter '*.dll' | Where-Object Name -NotLike 'e_sqlite3*' | ForEach-Object { Add-Type -Path $_.FullName }
[SQLitePCL.Batteries_V2]::Init()
Get-ChildItem 'C:\ProgramData\8005' -Directory -Filter 'ControlServer.V2*' | ForEach-Object {
  $f = Join-Path $_.FullName 'data\controlserver.db'
  if (-not (Test-Path -LiteralPath $f)) { return }
  "== $($_.Name)"
  $c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$f;Mode=ReadOnly")
  try {
    $c.Open()
    $cmd = $c.CreateCommand()
    $cmd.CommandText = 'SELECT UpperId, Status, OrderId, CreatedAt FROM OrderIntents WHERE UpperId LIKE $p OR OrderId = $o'
    $null = $cmd.Parameters.AddWithValue('$p', "$UpperIdPrefix%")
    $null = $cmd.Parameters.AddWithValue('$o', $OrderId)
    $r = $cmd.ExecuteReader()
    while ($r.Read()) { "  $($r.GetValue(0)) | $($r.GetValue(1)) | $(if ($r.IsDBNull(2)) { '' } else { $r.GetValue(2) }) | $($r.GetValue(3))" }
    $r.Close()
  } catch { "  ERR $($_.Exception.Message)" } finally { $c.Close() }
}
