# cs#664 计划快照腿序号从 1 连续：测试证据

分支 `fix/cs664-plan-leg-sequence`，实现提交 `3e620269e`（基于 `fp/v2-impl@70456eb1c`）。本机 2026-10-12 跑，变异时段经调度放行。

## 绿

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| `20-green-after-restore.txt` | 变异全部还原、重新构建后：`UpcomingStopPlanWireSequenceTests`、`ChargingClearanceToWaitingPointTests`、`OnboardJourneyPublisherTests`、`JourneyPlanCharacterizationTests`、`MultiStopPlanNineLegs`、`ArchitectureTests` | 172 条全过，退出码 0 |
| `21-green-wire-pins-and-append.txt` | 用零变化线上钉子的 `Batch7StopDrivenAdvanceWireParityTests`、`JourneyRuntimeWorkerTests`，以及 `EnRouteAppend`、`ClearanceMove`、`IdleReturnExecutionTests` | 153 条全过，退出码 0 |

两份都在出站 schema 门禁夹具下跑，夹具这次多了旧车载端规则；退出码 0 说明这些用例发出的每一张计划都满足「排序后等于 1..n」。

## 变异（每轮替换恰好一处、`git diff --stat` 只有一个文件、构建 0 错误，跑完 `git checkout` 还原）

| 文件 | 变异 | 红的用例 | 退出码 |
| --- | --- | --- | --- |
| `11-…M1-pre-fix-payload` | 载荷直接用投影的腿（即修前） | 暂存编号 4 条（`[2]`、`[1,3]`、`[1,10]`、`[3,1]`）、重发判定 1 条、清桩第一次与第二次 2 条，共 7 条 | 1 |
| `12-…M2-no-nine-leg-cap` | 去掉 9 条上限 | `NineLegsAreNumberedOneToNineAndATenthIsRefusedHere` | 1 |
| `13-…M3-resend-check-compares-raw-projection` | 重发判定比原投影 | `TheReSendCheckComparesWithTheRenumberedLegs` | 1 |
| `14-…M4-guard-rule-disabled` | 门禁规则恒判通过 | `TheGuardAppliesTheOldOnboardRule` 的 `[2]`、`[1,3]`、`[1,1]` | 1 |
| `15-…M1-guard-alone-on-an-existing-test` | 同 M1，只跑一条原有、不断言序号的清桩用例 | 无用例失败（`Failed: 0`），`Test Assembly Cleanup Failure` | 1 |

第 15 份说明门禁独立起作用：没有任何断言看序号，整轮仍因发出 `[2]` 的收尾计划而非 0。同一轮的 schema 报告是 0 处违规，所以清理失败来自旧车载端规则——这一点是排除法推的，异常原文在 TRX 里，控制台没打出来。

修前红：M1 就是修前的载荷，清桩两条用例在 M1 下红，即「修前红」。

## 审查之后（S1）

审查 S1：重新编号在发布路径（`PublishUpcomingStopPlanAsync`）上没有用例守着，审查的变异 M7 存活。补了 `APublishedTransportPlanWithAGapInItsStopsReachesTheVehicleNumberedFromOne`：停靠序号 `[1,3]` 的搬运计划由 `JourneyPlanBuilder.Plan` 投影出来、走发布路径，车收到的那一行和发件箱里存的那一行都是 `[1,2]`。

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| `16-mutation-M7-publish-path-not-renumbered.txt` | 在 `eeb7768a2` 上把发布路径的载荷换成不重新编号的投影原样，替换 1 处，构建 0 错误 | 新用例红（线上 `[1,3]`），退出码 1 |
| `22-green-after-review-s1.txt` | 还原、重新构建后，定向集合加上 `Batch7PlanRevisionTests` | 195 条全过，退出码 0 |

插曲：第一版新用例借了 `Batch7PlanRevisionTests` 的辅助方法，腿 id 和需求 id 不是 UUID。第一次跑出的结果是 195 条全过、退出码 1，原因是出站 schema 门禁拦下了这条用例自己发出的报文（`movementLegId` 要求 `uuid` 格式）。改用 UUID 之后，上表两份是重跑的结果。
