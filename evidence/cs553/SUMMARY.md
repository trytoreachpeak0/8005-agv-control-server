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

待补：本机全量自检（挂 `dotnet-counters`）与修后一轮 CI 的 trx 对照。
