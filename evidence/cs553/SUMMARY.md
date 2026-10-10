# control-server#553 证据索引

服务端全量测试在 vm01 上只用约 1.65 核、`Test` 步骤 36 分钟。本票先剖析、再修。

## 第一步：剖析（2026-10-10，本机未跑重负载）

| 文件 | 内容 |
| --- | --- |
| `step1-profile/run-38012693987-analysis.txt` | 修前一轮 CI 的 trx 分析：每类耗时、并发时间线、耗时分布、单条耗时对并发数的回归、按夹具分类 |
| `step1-profile/run-38013503611-analysis.txt` | 同上，另一轮。该轮提交 `95fc8c11` 与本票基点 `3411887d` 在 `src/`、`tests/` 下无差异，是本票的修前基线 |
| `step1-profile/Cs553Probe.cs.txt` | 一次性探针测试的源码（没有进测试项目）：量一次 `MigrateAsync` 的墙钟、分配量、GC 暂停、`Monitor` 争用次数，以及 2 路、4 路并行 |
| `step1-profile/probe-gcserver0.txt` | 探针在默认工作站 GC 下的输出（本机 Ryzen 5 7535U） |
| `step1-profile/probe-gcserver1.txt` | 探针在 `DOTNET_gcServer=1` 下的输出 |
| `step1-profile/local-small-class-before-after.txt` | `WaitingJourneyDashboardTests` 修前、修后在本机单跑的每条耗时 |
| `tools/*.py` | 上面分析用的脚本：`trx.py` 解析 trx；`reg.py` 回归；`conc.py` 按并发分桶；`classify.py` 按夹具分类（修前分类对着基点 `3411887d` 的源码跑） |

结论见 PR 正文的六格表。要点：

- 一次 `MigrateAsync` 在本机 0.8～1.0 秒、分配 190 MB，其中 GC 暂停占 30～37%；4 路并行时占 52%，且出现 145 次托管锁争用。
- vm01 上单条耗时 ≈ 1.2 秒 ×（同时在跑的条数），截距约 0，两轮 R²=0.88。每条跑迁移的测试约有 1.1～1.2 秒的工作在排队，合计约 2100 秒，就是测试墙钟。
- 从迁移好的模板库复制一份只要 2.7 毫秒，不分配内存。

## 第二步：修后对照

### 本机全量（`35c89b3a`，2026-10-10 13:48:59 → 13:56:19）

| 文件 | 内容 |
| --- | --- |
| `step2-local-full/console-tail.txt` | 控制台结尾：`Failed: 0, Passed: 4664`，测试阶段 5 分 31 秒；含构建共 7 分 20 秒 |
| `step2-local-full/compare-vs-38013503611.txt` | 与基线 run 38013503611 的 trx 逐条对账（脚本 `tools/compare.py`） |

- 测试名集合：基线有、修后没有的 0 条；修后多出 7 条，全是护栏类 `MigratedDatabaseTemplateTests`；两边共有的测试结果逐条相同。
- 3 条跳过的测试修前修后相同：`ReconnectModelTests` 的 `ReplaySequences`、`ReplaySeeds`、`PrototypeMeasurement`，都标了 `[Fact(Explicit = true)]`，trx 里记为 `NotExecuted`。控制台汇总行写 `Skipped: 0`，是因为汇总不把 Explicit 算作跳过，控制台上面逐条打印了这三条。
- 三个重点类（基线是 vm01，这里是本机，只能比量级）：`IdleReturnExecutionTests` 892 → 68 s；`VehicleFaultIsolationTests` 1050 → 1 s；`EmergencyStopSupervisorTests` 579 → 1 s。所有测试耗时之和 38074 → 3694 s。修后最长的类是 `InTransitDoorEmergencyReleaseTests`，173 s。
- **这一轮的 `dotnet-counters` 作废，没有入库**：监视脚本接到的是 VSTest 的外壳进程 `testhost.exe`，而 xunit v3 实际在子进程 `ControlServer.Tests.exe` 里跑测试，采到的是外壳的数据（CPU 平均 0.1%）。

### 修后 CI（以这一轮为准，#558 合入前）

| 文件 | 内容 |
| --- | --- |
| `step3-ci/compare-38040042973-vs-38013503611.txt` | 修后 run 38040042973（`c25cce44`）与基线 run 38013503611 的 trx 对账，按「类名、测试名、结果」的多重集合逐条比对 |
| `step3-ci/summary-lines.txt` | 两轮作业日志里 `dotnet test` 的汇总行，以及逐条打印的 3 条跳过 |

- `Test` 步骤：修前 36 分 00 秒，修后 7 分 31 秒。汇总行的持续时间：34 m 39 s → 6 m 13 s。
- 修前有、修后没有的 0 条；修后多出 7 条，全是 `MigratedDatabaseTemplateTests`。3 个参数化测试名在两边各出现 2 次，也逐条对上了。
- 两轮汇总行都写「已跳过: 0」，而两轮都逐条打印了同样 3 条 Explicit 测试被跳过，口径一致。
- `tools/compare.py` 第一版把测试名当作字典的键，重名条目只比其中一条；本地全量那份对账（`step2-local-full/`）是用第一版跑的。现在这一版改成多重集合比较。

## 跟进（#558 审查后）

| 文件 | 内容 |
| --- | --- |
| `followup/m452-mutation.patch` | 在 WAL 下复核 cs#452 写锁护栏用的变异（产品代码，只在本地做，没有提交）：入站处理器不在锁外观察，改在收件箱写事务里观察；`RiotReadOutsideWriteLock.Ensure` 失效 |
| `followup/m452-wal.txt` | 变异下 `FieldConfirmationWriteLockTests`，库是 WAL（#558 合入后的状态）：9 条红 7 条，其中两条写锁用例报 `SQLite Error 5: 'database is locked'` |
| `followup/m452-delete.txt` | 同一变异，另把多车夹具的文件库临时改回 `delete` 日志模式（#558 之前的状态；这个临时改动不在 patch 里）：红的 7 条完全相同 |
| `followup/m452-incomplete-first-attempt.txt` | 第一版变异不完整：锁外那次观察还在，RIoT 停在锁外那一次读，写锁两条仍是绿的。保留下来，说明为什么要把锁外观察也去掉 |

结论：#558 把这个文件库从 `delete` 变成 WAL 以后，cs#452 的写锁护栏仍然能红，红法和以前一样。
