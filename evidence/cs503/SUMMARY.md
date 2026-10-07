# control-server#503 证据

全部在本机离线完成，库都是临时目录里的临时库；没有碰任何服务、生产库或现场机器。

| 文件 | 内容 |
| --- | --- |
| `wal-probe.ps1`、`wal-probe.out` | `--migrate-only`（连接串经环境变量 `ConnectionStrings__ControlServer` 指向临时库）建出的库文件头第 18、19 字节为 `2,2`；一个常开的写连接提交 50 行后，只拷 `.db` 打开是 0 行且 `integrity_check` 为 `ok`，带 `-wal` 是 50 行，`VACUUM INTO` 是 50 行；连接全关后 `-wal`、`-shm` 消失 |
| `wal-probe2.ps1`、`wal-probe2.out` | 有读事务时拷 `-shm` 报 `locked a portion of the file`；`.instance-lock` 被 `FileShare.None` 持有时整目录 `Copy-Item -Recurse` 失败 |
| `snippet-check.ps1`、`snippet-check.out` | `docs/field/control-server-database-copy.md` 里的 `VACUUM INTO` 片段在写连接开着时导出，行数与在线一致，导出文件为 `delete` 模式 |
| `self-test.txt` | `scripts/Test-DataRootLockWait.ps1` 在本提交上的输出，30 项全部 PASS，退出码 0 |
| `mutations.txt` | 5 个变异（去掉升级前的等待、去掉回滚前的等待、占着也当已释放、去掉安装回滚前的等待、以读写方式开锁文件），每个都让自检退出码为 1，并列出变红的检查；每个变异后 `git checkout` 还原，最后工作树干净 |
| `g3-store-hash-check.ps1`、`.out` | 改过的四个 `.ps1` 用 `ParseFile` 解析 0 错误；`Get-StoreFilesSha256` 对「只有主文件」「带 wal A」「带 wal B」给出三个不同的值，重复计算稳定 |

WAL 的来源：Microsoft.EntityFrameworkCore.Sqlite 8.0.30 的 `SqliteDatabaseCreator.Create()` 执行
`PRAGMA journal_mode = 'wal';`（dotnet/efcore `release/8.0` 的 `src/EFCore.Sqlite.Core/Storage/Internal/SqliteDatabaseCreator.cs`；
本机 nuget 缓存里的程序集也含这条字面量；复测时 `--migrate-only` 输出的第 2 行就是它，那份输出未入库）。
