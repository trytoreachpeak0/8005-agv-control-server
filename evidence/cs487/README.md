# cs#487 证据：推进循环逐车隔离

本机 Windows，SDK 8.0.425，`-c Release`。每次运行都看 `dotnet test` 的退出码，不只看汇总行。

## 修前的红（提交 `d938a56e`，只动 tests）

`AVehicleWhoseAdvanceThrowsEveryRoundDoesNotStopTheOthersSupervisionOrTheDispatchRound`：

```
Assert.Single() Failure: The collection was empty
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```

退出码 1。红在第二台车的故障表为空：第一台车每轮抛出注入的 `InvalidOperationException`，整轮中止，第二台车的在途单 FAILED 没被记下，急停没发，派车轮没跑。

对照（临时用例，未入库）：同一布局不注入异常，第二台车两轮内记下 `SuspectedBlocked/OrderFailed` 并升级、RIoT 收到 `TriggerEmergency`、派车结论多 2 条，第一台车出现在 `CompletedVehicles` 里。退出码 0。所以「失败那台车不进派车结论」这条断言有区分力。

## 修后

三条新用例 `Passed: 3`，退出码 0。

## 反向验证（`reverse/`）

每个变异都从修后的文件按内容改出，`--no-incremental` 重编，只跑三条新用例；结束后按内容写回并完整重编。预期是先写后跑的。

| 变异 | 预期红 | 实际红 |
| --- | --- | --- |
| M1 非连接类失败改回整轮 `throw` | 第一条 | 第一条 |
| M2 失败车不进 `yielded` | 第一条 | 第一条 |
| M3 去掉撤回 | (i)、(ii) | (i)、(ii) |
| M4 去掉重读 | (ii) | (i)、(ii) |
| M5 只重读 `Modified` 状态的条目 | (ii) | (ii) |
| M6 去掉轮末重新抛出 | 三条 | 三条 |
| M7 不解除推进中新加进跟踪的条目 | (ii) | (ii) |

(i) = `WhatAVehicleWhoseAdvanceThrowsHadStagedOnAnotherRowIsNotSavedLaterInTheRound`，(ii) = `WhatAVehicleWhoseAdvanceThrowsSavedInARolledBackTransactionIsNotTakenAsFact`。

M4 多红了 (i)：推进前状态为 `Unchanged` 的条目，不论是暂存了未保存的改动，还是在回滚的事务里存过，`RestoreAsync` 都只靠重读恢复，不按快照回写值。去掉重读后 (i) 暂存的改动也没人撤，被后面的保存带进库。预期写漏了这一层，产品行为是对的。

## 与本票无关的既有不稳

`WhenAVehicleExhaustsItsBudgetTheHookIsStillCalledOnceWithOnlyTheVehiclesThatFinished` 在本机定向跑时红过（第二台车也超出 1 s 预算）。它只跑第一轮派车，那时没有旅程，本票改的循环不会进入。把产品文件换回 `fp/v2-impl` `f4f494ba` 的内容后同样连跑 5 遍，红 2 遍；修后连跑 5 遍红 4 遍。两边都是小样本，判为既有不稳（用真实计时器，cs#372 剩余风险第 11 条点过名），不在本票改。
