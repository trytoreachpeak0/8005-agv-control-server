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

   **没有锁文件时**（control-server#473 之前的构建不建它），按安装目录的路径查本实例的进程，没有输出才算退出了：

   ```powershell
   Get-Process ControlServer.Host -ErrorAction SilentlyContinue | Where-Object Path -like '<安装目录>\*'
   ```

   **不要按名字结束进程**（`Stop-Process -Name`、`taskkill /IM`）：10-08 起 factory01 上 MVP 与 v2 的 Host 同名、同时在跑，
   按名字会连生产 MVP 一起停掉。要结束就按上面查出的 PID：`Stop-Process -Id <PID>`。
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

## 升级的回滚被拒之后

`Update-ControlServerLocal.ps1` 失败时会自己回滚；回滚前同样要等库锁释放。等不到时它不碰安装目录与数据根，以
`ControlServer upgrade and rollback both failed.` 退出，报错里的 `DATA_ROOT_IN_USE` 那一段写着本次的备份目录
`<BackupRoot>\<runId>-upgrade`（下称 `<备份>`）。**重新执行升级脚本不是恢复**，那是再升级一次。按备份手工恢复，四步：

1. 按路径确认本实例的 Host 已经退出（上面第 2 步的 `Get-Process … | Where-Object Path -like '<安装目录>\*'` 没有输出）。
2. 用 `<备份>\install` 整体替换安装目录、用 `<备份>\data-root` 整体替换数据根：先清空目标目录，再把备份里的内容全部拷回
   （数据目录里的 `.db`、`-wal`、`-shm` 一起）。
3. 证书密码变量：如果本次升级删除过机器级变量（默认 `CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD`），回滚已先把它恢复，
   诊断文件里有 `certificate-password-restored` 一行；没有这一行而升级前有这个变量时，手工恢复为升级前的值。报错原文会说明本次是哪种情况。
4. `Start-Service '<服务名>'`，读回 `/health/live`。

`Install-ControlServerLocal.ps1`（首次安装）的回滚被拒时，报错里同样写着该怎么手工收尾：删除安装目录，数据根按报错
所说清空、按备份拷回或原样不动；它注册的服务已在回滚里删掉，不用起服务。

如果升级停服之后旧进程迟迟不退，升级会在**备份之前**就中止，安装目录与数据根都没动；这时脚本随后的起服多半也会失败，
看到的很可能同样是 `upgrade and rollback both failed`。按报错里的说明处理：按路径确认进程退出，把服务起回来，再重新升级。
