# control-server#339 零变化基线：只多了 WorklistRefills=0

在 `523d0723`（merge `012c31b2` 并生成迁移之后）上，用 ZeroChangePin 的十份终结状态基线的类（`JourneyRuntimeWorkerTests` 等 9 个类）跑出 10 条红，全部是同一处差异：

```
Expected: ···"-e852-b1db-2ac154e412ff'|Status='PENDING'|CreatedA"···
Actual:   ···"-e852-b1db-2ac154e412ff'|WorklistRefills=0|Status="···
```

改法：只在 `## JourneyStops` 一节的每一行、`|Status=` 之前插入 `|WorklistRefills=0`（脚本断言每行恰好一个 `|Status=`、原来没有 `WorklistRefills`）。之后这 9 个类 184 条全绿。

判据（与 cs#273 同形）：把新基线里的 `|WorklistRefills=0` 删掉，与 `origin/fp/v2-impl`（`012c31b2`）上的旧基线逐字相同。

```
cancellation-before-sublot.txt occurrences=2 same
commanded-ending-FaultCargoRecoveryResult.txt occurrences=2 same
commanded-ending-LoadCancellationResult.txt occurrences=2 same
commanded-ending-LoadCompensationResult.txt occurrences=2 same
dashboard-blocked-journeys-admission-session-down.txt occurrences=0 same
dashboard-blocked-journeys-own-move-order.txt occurrences=0 same
dashboard-expected-action-overdue-load.txt occurrences=0 same
dashboard-expected-action-overdue-unload.txt occurrences=0 same
determinate-load-failure.txt occurrences=2 same
fault-cargo-handoff.txt occurrences=2 same
forced-mechanical-recovery.txt occurrences=2 same
in-flight-cancellation.txt occurrences=2 same
station-deadline.txt occurrences=2 same
unload.txt occurrences=2 same
```

应有 20 处（十份各两行停靠，先数过旧基线里每份的 JourneyStops 行数），实数 20 处；十四份删掉新字段后全部与集成分支相同。
