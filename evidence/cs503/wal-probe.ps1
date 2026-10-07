#Requires -Version 7
# cs#503 probe: scratch DB only. Never points at a real data root.
$ErrorActionPreference = 'Stop'
$bin = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/b503-8005-agv-control-server/src/ControlServer.Host/bin/Release/net8.0/win-x64'
$root = Join-Path $PSScriptRoot 'wal-probe'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$data = New-Item -ItemType Directory (Join-Path $root 'data')
$db = Join-Path $data 'controlserver.db'

function Show-Files($label) {
    "--- $label"
    Get-ChildItem $data | ForEach-Object { '{0,-36} {1,10}' -f $_.Name, $_.Length }
}
function Header($path) {
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    $b = [byte[]]::new(20); [void]$fs.Read($b, 0, 20); $fs.Dispose()
    "header[18],[19] of $(Split-Path $path -Leaf) = $($b[18]),$($b[19])"
}

"=== A. --migrate-only on scratch DB"
$env:ConnectionStrings__ControlServer = "Data Source=$db"
$p = Start-Process "$bin/ControlServer.Host.exe" -ArgumentList '--migrate-only' -WorkingDirectory $bin -PassThru -NoNewWindow -RedirectStandardOutput "$root/migrate.out" -RedirectStandardError "$root/migrate.err" -Wait
"migrate-only exit code: $($p.ExitCode)"
Header $db
Show-Files 'after --migrate-only exited'

Add-Type -Path "$bin/SQLitePCLRaw.core.dll"
Add-Type -Path "$bin/SQLitePCLRaw.batteries_v2.dll"
Add-Type -Path "$bin/SQLitePCLRaw.provider.e_sqlite3.dll"
Add-Type -Path "$bin/Microsoft.Data.Sqlite.dll"
$env:PATH = "$bin;$env:PATH"
[SQLitePCL.Batteries_V2]::Init()

function Exec($conn, $sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; $r = $c.ExecuteScalar(); $c.Dispose(); $r }
function Count($path) {
    $c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$path;Pooling=False")
    $c.Open()
    try { "rows=$(Exec $c 'SELECT count(*) FROM cs503_probe') journal_mode=$(Exec $c 'PRAGMA journal_mode') integrity=$(Exec $c 'PRAGMA integrity_check')" }
    catch { "open/read failed: $($_.Exception.Message)" }
    finally { $c.Dispose() }
}

"=== B. a long-lived writer (stands in for the running Host)"
$a = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False")
$a.Open()
"journal_mode seen by a fresh connection: $(Exec $a 'PRAGMA journal_mode')"
[void](Exec $a 'CREATE TABLE cs503_probe (id INTEGER PRIMARY KEY, v TEXT)')
"checkpoint after create: $(Exec $a 'PRAGMA wal_checkpoint(TRUNCATE)')"
foreach ($i in 1..50) { [void](Exec $a "INSERT INTO cs503_probe (v) VALUES ('committed-$i')") }
"committed rows on the live connection: $(Exec $a 'SELECT count(*) FROM cs503_probe')"
Show-Files 'live, after 50 committed single-row transactions'

$b1 = New-Item -ItemType Directory (Join-Path $root 'copy-main-only')
Copy-Item $db $b1
"copy main file only        -> $(Count (Join-Path $b1 'controlserver.db'))"

$b2 = New-Item -ItemType Directory (Join-Path $root 'copy-main-and-wal')
Copy-Item $db $b2; Copy-Item "$db-wal" $b2
"copy main + -wal           -> $(Count (Join-Path $b2 'controlserver.db'))"

$b3 = New-Item -ItemType Directory (Join-Path $root 'copy-shm')
try { Copy-Item "$db-shm" $b3; 'copy -shm: succeeded' } catch { "copy -shm: FAILED: $($_.Exception.Message)" }

$b4 = Join-Path $root 'copy-whole-dir'
try { Copy-Item $data $b4 -Recurse; 'Copy-Item -Recurse of live data dir: succeeded' } catch { "Copy-Item -Recurse of live data dir: FAILED: $($_.Exception.Message)" }

$v = Join-Path $root 'vacuum-into.db'
$r = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False"); $r.Open()
[void](Exec $r "VACUUM INTO '$($v.Replace('\','/'))'"); $r.Dispose()
"VACUUM INTO (live)         -> $(Count $v); $(Header $v)"

"=== C. writer closes (stands in for a clean service stop)"
$a.Dispose()
Show-Files 'after the last connection closed'
$b5 = New-Item -ItemType Directory (Join-Path $root 'copy-after-close')
Copy-Item $db $b5
"copy main only after close -> $(Count (Join-Path $b5 'controlserver.db'))"
