# cs#506 证据

修复续行（`RESUME_AFTER_REPAIR`）的新结果对上后，`OnboardRecoveryCoordinator.ObserveOperationResultAsync` 把旅程从 `Blocked`
放出时不看别的需求。本票让它与 cs#499 的终结放行共用 `BlockedJourneyRelease.NothingElseUnresolvedAsync`（第 3、4、5 条）。

全部是单元测试进程内的结果（真实入站处理器 + 真实引擎 + SQLite），不是 L2/G3，与真车、真 RIoT 无关。
摘要由 TRX 生成；原始 TRX 未入库。

| 文件 | 内容 |
| --- | --- |
| `red/01-ticket-chain-red-at-f5e6c5c9.txt` | 票面链条：D1 交接没对上、D2 修复续行对上，旅程被放出并发出 1 条离站核验 |
| `red/02-all-new-tests-red-at-c2f073e3.txt` | 修前全部新用例：4 红 1 绿（绿的是对照：只有 D2 待恢复时照常放车） |
| `green/01-class-green-at-d3fa6c11.txt` | 修后 `RecoveryEndingReleasesBlockedJourneyTests` 17/17（含 cs#499 的 12 格） |
| `green/02-g2-class-green-at-d3fa6c11.txt` | 修后 `RecoveryStateMachineG2Tests` 211/211（既有的修复续行覆盖） |
| `reverse/mutations-at-d3fa6c11.txt` | 三个变异，各被两格杀死（cs#499 一格、本票一格） |

## 变异

都改在 `src/ControlServer.Host/Runtime/BlockedJourneyRelease.cs` 的 `NothingElseUnresolvedAsync`，逐个改、`--no-incremental` 重建（0 错误）后跑整个类，
再用提交时的原文覆盖还原。

| 编号 | 改法 | 去掉的判据 |
| --- | --- | --- |
| M3 | `return !await dbContext.StationOperations...` → `return true \|\| !await ...` | 第 3 条：别的操作已收敛 |
| M4 | `item.Demand.Status == DemandExecutionStatus.RecoveryRequired` 后加 `&& false` | 第 4 条：别的需求没有待恢复 |
| M5 | `EndsWith("_NOT_RECONCILED", ...)` → `EndsWith("_MUTANT_NEVER_MATCHES", ...)` | 第 5 条：阻塞码不是 `*_NOT_RECONCILED` |

M5 的第一种改法（整个条件换成 `false`）编译不过（`if (false)` 被当作不可达代码报错），没有跑；表里是第二种改法。

## 第二轮：独立审查之后

审查必修 M-1：修复续行对上的装货不把需求从 `RecoveryRequired` 改回来，第 4 条把这个过时标记当成别的需求待恢复，同一旅程第二次恢复就卡住。
修法是在修复续行结果的那一次保存里改回 `Accepted`（`OnboardRecoveryCoordinator.ObserveOperationResultAsync`）。

| 文件 | 内容 |
| --- | --- |
| `red/03-review-m1-red-at-916efde2.txt` | 两格新用例（同一旅程第二次修复续行、修复续行之后交接另一条）在第一版产品代码上红，原因都是 `second-demand=RecoveryRequired` |
| `green/03-class-green-at-ef46b7d3.txt` | 修后 `RecoveryEndingReleasesBlockedJourneyTests` 19/19 |
| `green/04-g2-class-green-at-ef46b7d3.txt` | 修后 `RecoveryStateMachineG2Tests` 211/211（其中一格的断言由「仍是 `RecoveryRequired`」改为 `Accepted`） |
| `reverse/mutations-round2-at-ef46b7d3.txt` | M3、M4、M5 重跑仍各被两格杀死；M8 被三格杀死 |

| 编号 | 改法 | 去掉的东西 |
| --- | --- | --- |
| M8 | `if (resumedDemand?.Status == DemandExecutionStatus.RecoveryRequired)` → `== DemandExecutionStatus.Cancelled` | 修复续行对上后把需求改回 `Accepted` |

审查 S-1 之后，单需求「补偿没对上、再修复续行」那一格的补偿结果改成现场形状（`UNKNOWN`，恢复报告检查点 `ACTIVE_UNLOCK_SET`），修后仍绿、仍被 M5 杀死。
