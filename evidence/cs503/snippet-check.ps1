#Requires -Version 7
$bin = 'C:\Users\szy\Desktop\8005-workspace-v2\worktrees\b503-8005-agv-control-server\src\ControlServer.Host\bin\Release\net8.0\win-x64'
$db = "$PSScriptRoot\wal-probe\data\controlserver.db"
$out = "$PSScriptRoot\wal-probe\snippet-export.db"
Remove-Item $out -ErrorAction SilentlyContinue
foreach ($d in 'SQLitePCLRaw.core', 'SQLitePCLRaw.batteries_v2', 'SQLitePCLRaw.provider.e_sqlite3', 'Microsoft.Data.Sqlite') {
    Add-Type -Path "$bin\$d.dll"
}
$env:PATH = "$bin;$env:PATH"; [SQLitePCL.Batteries_V2]::Init()
$w = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False"); $w.Open()
$x = $w.CreateCommand(); $x.CommandText = "INSERT INTO cs503_probe (v) VALUES ('live')"; $null = $x.ExecuteNonQuery()
"wal present while live: $(Test-Path "$db-wal")"
$c = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Mode=ReadOnly;Pooling=False")
$c.Open(); $cmd = $c.CreateCommand(); $cmd.CommandText = 'VACUUM INTO $p'
$null = $cmd.Parameters.AddWithValue('$p', $out); $null = $cmd.ExecuteNonQuery(); $c.Dispose()
$r = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$out;Pooling=False"); $r.Open()
$q = $r.CreateCommand(); $q.CommandText = 'SELECT count(*) FROM cs503_probe'; "exported rows: $($q.ExecuteScalar())"
$q.CommandText = 'PRAGMA journal_mode'; "exported journal_mode: $($q.ExecuteScalar())"; $r.Dispose()
$q2 = $w.CreateCommand(); $q2.CommandText = 'SELECT count(*) FROM cs503_probe'; "live rows: $($q2.ExecuteScalar())"; $w.Dispose()
