# cs#643 证据：同站中间批次闭环后离站等待重新计时

本机 Windows，SDK 8.0.425，Debug。每次运行都看 `dotnet test` 的退出码，不只看汇总行。文件是从控制台输出精简出来的，只留失败用例名、失败原文和汇总行。

## 探针（`probe/`）

| 文件 | 跑在哪 | 结果 |
| --- | --- | --- |
| `01-middle-batch-at-ceb7804ed.txt` | `ceb7804ed`（修前，只加了探针） | 一站两条、等待 60s、装货 40s：闭环后起点仍是到站时刻，车上清单 r2 的期限也是到站 + 60s；闭环后 20s 本站被期限结束，还没录的那一条 `TERMINATED` |
| `02-append-at-standing-station-at-ceb7804ed.txt` | `ceb7804ed`（票外，只观测） | 车停站时同站追加：新需求成为当前停靠之后的新停靠；车在原停靠等到原期限才离站，新停靠「到站」时拿到完整 60s |

探针 01 第一次跑时本站没被结束，原因是用例没证明门关着（`TryEndStopAtStationDeadlineAsync` 门未证明关闭时不结束）。补上 `ProveSlotDoorsClosedAsync` 后才是上表的结果；文件是第二次的输出。

## 修前的红（`red/`）

`R0-pre-fix-engine.test.txt`：引擎这一支换回修前的写法（`if (resumingAfterCommit || resumingAfterEnding) ??= now`），tests 为本票版本。
5 红 7 绿：红的是中间批次、期限结束时刻、#289 崩溃续跑、断联作废后正常落定（反转后的那一条）、#499 子情形；
绿的是护栏（最后一批、一条都没装上就结束、纠错按阶段拒收的五行），它们钉的是本票不改的行为。

## 反向验证（`reverse/`）

每个变异都从修后的文件按内容改出，跑本票的定向用例；结束后按字节写回，`git diff -- src` 只剩本票那一处。
MX2 在审查（`bc91e401`）之后补上崩在录入请求写入那一条用例时跑，过滤条件另含 `ResumeAfterAcknowledgedReconnectTests`（共 17 条）。MR1、MR3、MR4、MC1、MC2 跑时 #499 子情形的用例还没写（共 11 条）；R0、MR2 在补上它之后重跑（共 12 条）。

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| MR1 | 一条都没装上就结束（`resumingAfterEnding`）也重置起点 | `AnEndedBatchWithAnotherStillToLoadDoesNotRestartTheWait` |
| MR2 | #289 续跑不重置、只有正常落定重置 | `AResumeAfterACrash…`、`ACompensationWithOneLoadedAndOneStillToLoad…` |
| MR3 | 重置挪到发清单之后 | `AMiddleBatch…TheVehicleHoldsTheSameDeadline`、`AFreshLoadAfterAVoidedWait…`、`AResumeAfterACrash…` |
| MR4 | 最后一批也只补填、不重置 | `TheLastBatchStillRestartsTheWaitAtItsOwnClosure` |
| MC1 | 纠错授权放宽到 `AwaitingSublot` | 纠错按阶段拒收那一行 `AwaitingSublot` |
| MC2 | 纠错授权放宽到 `AwaitingLoadResult` | 纠错按阶段拒收那一行 `AwaitingLoadResult` |
| MX2 | 删掉续跑那一支里 `AdvanceWorklistPastAStaleDeadlineAsync` 的升号调用（审查 L1） | `AResumeAfterTheWorklistWasStored…`（审查后补）、原有的 `WithTheStationDeadlineOn…AfterTheReconnect` |

## 定向回归

修后在本票分支上跑 23 个测试类（本票用例、cs#637 的 `UnloadStopDepartureWaitTests`、cs#633 的 `LoadedStopDeadlineReachesVehicleTests`、
同一块代码周边的多需求与续跑用例、架构测试，以及调用 `ZeroChangePin` 的八个类）：`Passed: 707, Failed: 0`，退出码 0。
`ZeroChangePins/` 下没有任何文件变化，不需要重录。全量交给 CI。
