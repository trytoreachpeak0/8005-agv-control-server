#Requires -Version 7
$ErrorActionPreference = 'Stop'
$bin = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/b503-8005-agv-control-server/src/ControlServer.Host/bin/Release/net8.0/win-x64'
$data = Join-Path $PSScriptRoot 'wal-probe/data'; $db = Join-Path $data 'controlserver.db'
foreach ($d in 'SQLitePCLRaw.core','SQLitePCLRaw.batteries_v2','SQLitePCLRaw.provider.e_sqlite3','Microsoft.Data.Sqlite') { Add-Type -Path "$bin/$d.dll" }
$env:PATH = "$bin;$env:PATH"; [SQLitePCL.Batteries_V2]::Init()
function Exec($conn, $sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; $r = $c.ExecuteScalar(); $c.Dispose(); $r }
$a = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False"); $a.Open()
[void](Exec $a "INSERT INTO cs503_probe (v) VALUES ('x')")
$r = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False"); $r.Open()
$tx = $r.BeginTransaction(); $c=$r.CreateCommand(); $c.Transaction=$tx; $c.CommandText='SELECT count(*) FROM cs503_probe'; "reader in open read txn sees $($c.ExecuteScalar())"
$out = Join-Path $PSScriptRoot 'wal-probe/copy2'; New-Item -ItemType Directory $out -Force | Out-Null
try { Copy-Item "$db-shm" $out -Force; 'copy -shm during a read transaction: succeeded' } catch { "copy -shm during a read transaction: FAILED: $($_.Exception.Message)" }
$tx.Dispose()
$lock = [IO.File]::Open("$db.instance-lock", 'OpenOrCreate', 'ReadWrite', 'None')
try { Copy-Item $data (Join-Path $PSScriptRoot 'wal-probe/copy3') -Recurse; 'Copy-Item -Recurse with instance-lock held: succeeded' } catch { "Copy-Item -Recurse with instance-lock held: FAILED: $($_.Exception.Message)" }
$lock.Dispose(); $r.Dispose(); $a.Dispose()
