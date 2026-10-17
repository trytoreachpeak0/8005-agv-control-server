#Requires -Version 7
# READ-ONLY: copy each V2 database (current C and every set-aside ControlServer.V2.* root; never the MVP one) to a temp
# folder, then query MAX(DispatchGeneration) in four tables on the copy, Mode=ReadOnly, DLLs from the sqlite-tools copy.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$tools = 'D:\zhengyushao\control-server-v2-ops\sqlite-tools'
Get-ChildItem $tools -Filter '*.dll' | Where-Object Name -NotLike 'e_sqlite3*' | ForEach-Object { Add-Type -Path $_.FullName }
[SQLitePCL.Batteries_V2]::Init()
$tmp = 'D:\zhengyushao\control-server-v2-ops\prod-20261014\gen-copies'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$sql = "SELECT 'OrderIntents', MAX(DispatchGeneration) FROM OrderIntents UNION ALL SELECT 'JourneyRuntimes', MAX(DispatchGeneration) FROM JourneyRuntimes UNION ALL SELECT 'JourneyDemands', MAX(DispatchGeneration) FROM JourneyDemands UNION ALL SELECT 'RiotDispatchAuditEvents', MAX(DispatchGeneration) FROM RiotDispatchAuditEvents"
Get-ChildItem 'C:\ProgramData\8005' -Directory | Where-Object { $_.Name -eq 'ControlServer.V2' -or $_.Name -like 'ControlServer.V2.*' } | ForEach-Object {
  $src = Join-Path $_.FullName 'data\controlserver.db'
  if (-not (Test-Path -LiteralPath $src)) { return }
  $dst = Join-Path $tmp "$($_.Name).db"
  foreach ($sfx in '', '-wal', '-shm') { if (Test-Path -LiteralPath "$src$sfx") { Copy-Item -LiteralPath "$src$sfx" -Destination "$dst$sfx" -Force } }
  $c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dst;Mode=ReadOnly")
  $parts = @()
  try {
    $c.Open(); $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $r = $cmd.ExecuteReader()
    while ($r.Read()) { $parts += "$($r.GetValue(0))=$(if ($r.IsDBNull(1)) { 'null' } else { $r.GetValue(1) })" }
    $r.Close()
  } catch { $parts += "ERR $($_.Exception.Message)" } finally { $c.Close() }
  "$($_.Name): $($parts -join ' ')"
}
$i = Get-Content -Raw 'D:\zhengyushao\control-server-v2-ops\installed-instance.json' | ConvertFrom-Json -AsHashtable
$cfg = Get-Content -Raw 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' | ConvertFrom-Json -AsHashtable
"installed-instance: name=$($i.name) phase=$($i.productionCutover?.phase) definition.journeyRuntime.dispatchGeneration=$($i.journeyRuntime.dispatchGeneration ?? '(absent: default)') zones=$($i.journeyRuntime.allowedDispatchZones -join ',')"
"appsettings.Production.json JourneyRuntime.dispatchGeneration=$($cfg.JourneyRuntime.dispatchGeneration ?? '(absent: package default)')"
Remove-Item -LiteralPath $tmp -Recurse -Force
