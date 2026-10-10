# control-server#546 合成 L2 证据（批次10-02，2026-10-10，本机）

六个新场景的本机运行、批次10-01 合入前的红绿对照、按侧取仓的注入。全部经
`Invoke-HeavyLocal.ps1 -Ticket cs#546` 调 `scripts/l2/Invoke-L2Scenario.ps1 -BatchId batch-10`，合成装置。

**精简**：每次运行只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl` 与 `snapshots/`；`logs/`（服务端日志单份最多 9 MB）没有入库，
`SUMMARY.md` 里提到 `logs/` 的那一行因此指向不存在的目录。

## green/：本分支（`588fc34c2`）

| 目录 | 结论 |
| --- | --- |
| `same-direction-die-to-wire-staging-journey-01` | PASS |
| `same-direction-die-to-oven-journey-01` | PASS |
| `same-direction-wire-to-optical-journey-01` | PASS |
| `same-direction-wire-to-nitrogen-journey-01` | PASS |
| `same-direction-binding-missing-not-cascading-01` | PASS |
| `same-direction-mixed-load-multi-drop-01` | PASS（含负向第二段） |
| `same-direction-binding-missing-not-cascading-02-after-merge-c94929302` | PASS——merge `fp/v2-impl@0c2c27eb` 之后（`c94929302`）重跑，覆盖 `756059339` 改过的 `L2-SDBM-01` 判据文字 |
| `same-direction-mixed-load-multi-drop-02-after-review-b802d3941` | PASS——审查 M1 改完（`b802d3941`），M2 变异还原、重新构建之后重跑 |
| `8b780a24-same-direction-binding-missing-not-cascading` | PASS——**批次10-01 合入前的提交 `8b780a24`** 上也是绿的，对照用：它测的是批次 6 已有的缺绑定机制 |

## red/：应当红、确实红的

| 目录 | 提交 | 红在哪 |
| --- | --- | --- |
| `8b780a24-same-direction-die-to-wire-staging-journey` | `8b780a24`（批次10-01 合入前） | `-REAR-01` 不受理，积压原因 `TASK_TYPE_NOT_YET_EXECUTABLE` |
| `8b780a24-same-direction-die-to-oven-journey` | 同上 | 同上 |
| `8b780a24-same-direction-wire-to-optical-journey` | 同上 | 同上 |
| `8b780a24-same-direction-wire-to-nitrogen-journey` | 同上 | 同上 |
| `swap-front-rear-same-direction-wire-to-optical-journey` | `588fc34c2` + 注入 | 分区归属表 `FRONT`／`REAR` 对调（`injection.diff`）：`-REAR-03`、`-FRONT-03`（目标仓在指派组）红，`-00`（前置核对读归属表）红，其余绿 |
| `m2-load-slots-from-first-demand-same-direction-mixed-load-multi-drop` | `b802d3941` + 未提交的产品代码变异（`injection.diff`：`StageLoadAsync` 的装货仓位改取本停靠第一单的目标仓，审查 M2） | `L2-SDMX-05`（丙，305）`LOAD [1]; UNLOAD [2]`、`L2-SDMX-06`（乙，13）`LOAD [1]; UNLOAD [5]` 红；甲那一站与其余判据绿。`SUMMARY.md` 里的 `controlServerCommit` 是 `b802d3941`，变异在工作区里、没有提交 |

旧提交上的运行是在 `8b780a24` 的 detached worktree 里放进本票六个场景文件跑的，编排器与产品代码都是那个提交的。
