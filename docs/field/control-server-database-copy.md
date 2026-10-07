# 服务端的库怎么拷、怎么还原

**库由三个文件组成：停服务后整目录拷贝，运行中用 `VACUUM INTO` 导出。**只拷 `controlserver.db` 一个文件是错的。

## 为什么不是一个文件

服务端的 SQLite 库是 WAL 模式：EF Core 建库时执行 `PRAGMA journal_mode = 'wal';`，之后一直如此
（control-server#503）。服务端运行时，数据目录 `<DataRoot>\data\` 里有：

| 文件 | 是什么 |
| --- | --- |
| `controlserver.db` | 主文件 |
| `controlserver.db-wal` | 已提交、还没写回主文件的改动。服务端正常停下时写回并删掉；进程被强杀时留下 |
| `controlserver.db-shm` | `-wal` 的索引 |
| `controlserver.db.instance-lock` | 服务端持有的库锁（control-server#473）。不要删：删它解不了锁 |

control-server#503 在临时库上实测：提交 50 行后，运行中只拷主文件，拷出来的库能打开、`PRAGMA integrity_check`
是 `ok`，但里面是 0 行；带上 `-wal` 一起拷是 50 行。**少了的行不会报错，只会悄悄不见。**

## 两种正确做法

**停服务后整目录拷贝（备份、搬迁、交给 G3 的 `-FieldRunRoot`）：**

1. `Stop-Service '<服务名>'`。
2. 确认进程真的退出了：服务管理器报「已停止」不等于进程已经关上库。最直接的证明是能独占打开锁文件：

   ```powershell
   [IO.File]::Open('<DataRoot>\data\controlserver.db.instance-lock', 'Open', 'Read', 'None').Dispose()
   ```

   报 `being used by another process` 就是还有进程占着，等几秒再试，别往下走。
3. 拷整个 `<DataRoot>\data\` 目录（`-wal`、`-shm` 在不在都一并拷）。

`Install-ControlServerLocal.ps1`、`Update-ControlServerLocal.ps1` 自己的备份与回滚就是这样做的：拷贝或删除数据根之前
等锁释放，最多 30 秒，等不到以 `DATA_ROOT_IN_USE` 中止，数据根原样不动。

**运行中导出一个单文件快照（不能停服务时）：**用 `VACUUM INTO`。它在一个读事务里把库连同 `-wal` 里的内容写成
一个新的、非 WAL 的单文件，导出文件不需要带 `-wal`、`-shm`。在服务端安装目录里执行（只读打开，不挡服务端写）：

```powershell
$bin = '<安装目录>'
foreach ($d in 'SQLitePCLRaw.core', 'SQLitePCLRaw.batteries_v2', 'SQLitePCLRaw.provider.e_sqlite3', 'Microsoft.Data.Sqlite') {
    Add-Type -Path "$bin\$d.dll"
}
$env:PATH = "$bin;$env:PATH"; [SQLitePCL.Batteries_V2]::Init()
$c = [Microsoft.Data.Sqlite.SqliteConnection]::new('Data Source=<DataRoot>\data\controlserver.db;Mode=ReadOnly;Pooling=False')
$c.Open(); $cmd = $c.CreateCommand(); $cmd.CommandText = 'VACUUM INTO $p'
$null = $cmd.Parameters.AddWithValue('$p', '<导出目录>\controlserver.db'); $null = $cmd.ExecuteNonQuery(); $c.Dispose()
```

导出路径必须是还不存在的文件。G3 的库生成器 `scripts/l2/scenarios/demand-bearing-store-at-unload.ps1` 用的是同一个做法。

## 还原

停服务、确认进程退出（同上第 2 步），清空 `<DataRoot>\data\`，把备份里的文件**一起**放回去，再起服务。不要把旧备份的
`controlserver.db` 和现场留下的新 `-wal` 混在一起：那个 `-wal` 不属于备份里的那个库。
