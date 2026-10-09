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

十一条新用例 `Passed: 11`，退出码 0。

1 = `AVehicleWhoseAdvanceThrowsEveryRoundDoesNotStopTheOthersSupervisionOrTheDispatchRound`
(i) = `WhatAVehicleWhoseAdvanceThrowsHadStagedOnAnotherRowIsNotSavedLaterInTheRound`
(ii) = `WhatAVehicleWhoseAdvanceThrowsSavedInARolledBackTransactionIsNotTakenAsFact`
(iii) = `AChangeLeftForALaterSaveThatTheFailingVehicleSavedAndRolledBackGoesBackToBeingThatChange`
P-C = `AChangeLeftForALaterSaveThatTheFailingVehicleSavedAndCommittedStaysSaved`
P-D = `ARowAddedForALaterSaveThatTheFailingVehicleInsertedAndCommittedIsNotInsertedAgain`
P-E = `ADeleteLeftForALaterSaveThatTheFailingVehicleSavedAndRolledBackIsStillCarriedOut`
Q1 = `AChangeLeftForALaterSaveThatTheFailingVehicleCommittedAndChangedAgainStaysAsCommitted`
Q2 = `ARowAddedForALaterSaveThatTheFailingVehicleCommittedAndChangedAgainIsNotInsertedAgain`
R = `WhenTheWithdrawalCannotReadARowAgainItsVehicleSitsOutThisRoundOnly`
S = `TheHostsShutdownWhileTheWithdrawalReadsARowAgainLeavesTheRound`

(iii)、P-C、P-D、P-E、Q1、Q2 直接测 `TrackedBeforeAdvance.RestoreAsync`，不经过一轮推进：今天没有哪一步推进在走完时留着未保存的改动，一轮推进造不出「推进前挂着改动」的条目。写 (iii) 时经 RIoT 替身试过两个注入点（按单号读订单、读车），用例里的前提断言两次都红在 `Expected: Modified / Actual: Unchanged`，所以改为直接测。独立审查插桩跑了 671 条用例，`Take` 时也都没带改动。

R 与 S 经命令拦截器让撤回时对旅程表的重读失败（R：前 3 次读失败；S：读时停机）。

## 反向验证（`reverse/`）

每个变异都从修后的文件按内容改出，`--no-incremental` 重编，只跑十一条新用例；结束后按内容写回并完整重编。预期是先写后跑的。下表是第四轮（增量审查意见之后，对最终产品代码重跑的全部格）。

| 变异 | 预期红 | 实际红 |
| --- | --- | --- |
| M1 非连接类失败改回整轮 `throw` | 1 | 1、R |
| M2 失败车不进 `yielded` | 1 | 1 |
| M3 去掉撤回 | (i)、(ii) | (i)、(ii)、R |
| M4 去掉重读 | (i)、(ii) | (i)、(ii)、R |
| M5 只重读 `Modified` 状态的条目 | (ii) | (ii)、R |
| M6 去掉轮末重新抛出 | 1、(i)、(ii) | 1、(i)、(ii)、R |
| M7 不解除推进中新加进跟踪的条目 | (ii) | (ii) |
| M8 退回时不回写原始值 | (iii) | (iii) |
| M9 推进前 Modified 的条目一律当已提交 | (iii) | (iii) |
| M10 推进前 Modified 的条目一律当已回滚 | P-C、Q1 | P-C、Q1 |
| M11 推进前 Added 的条目一律当已回滚 | P-D、Q2 | P-D、Q2 |
| M12 推进前 Deleted 的条目一律当已提交 | P-E | P-E |
| M13 重读失败时不解除跟踪 | R | R |
| M14 重读的 catch 连停机取消也吞掉 | S（不确定） | 存活 |
| M15 直接退回只比原始值、不比状态 | Q2 | P-D、Q2 |
| M16 直接退回只比状态、不比原始值 | Q1 | Q1 |

多红的 R 都对得上：M1 下第二轮第二台车仍推进不到，故障记不下来；M3、M4、M5 下撤回不再重读那 3 行，注入的失败读没被用掉，「失败读已用完」的前提断言红；M6 下整轮不再抛出原异常。

M15 多红了 P-D：Added 条目的原始值就是它的当前值，提交之后两者仍与快照相同，只比值就会把一个已插入的行退回成 Added。这正是改法要同时比状态的原因。

M14 存活：吞掉这一次取消之后，撤回里下一次读库、随后给失败车写码时的重读都带着已取消的令牌，照样抛出取消，停机仍然离开这一轮，用例分辨不出。过滤条件保留：它与引擎里每一处 catch 排除停机取消的写法一致，去掉它的效果只是撤回多解除几个条目的跟踪，结局相同。第一次跑 M14 编译失败（`CS0168`，变异写成了带名字却不用的 `catch (Exception error)`），改成 `catch (Exception)` 后单独重跑，结果如上。

早先三轮（M1–M7 三条用例；加 (iii) 与原 M8、M9；审查应改之后的 M1–M14 九条用例）的结果已被本轮取代，各格结论与本轮一致。第一轮里 M4 比预期多红了 (i)，原因是推进前 `Unchanged` 的条目只靠重读恢复，暂存的未保存改动也走这条路。

## 全量（`full/`）

`7649d656` 上的 Release 全量（审查意见之前的版本）：

```
Passed!  - Failed:     0, Passed:  4263, Skipped:     0, Total:  4263, Duration: 1 h 5 m - ControlServer.Tests.dll (net8.0)
```

退出码 0，无 `[Test Assembly Cleanup Failure]`。TRX：total 4266、executed 4263。未执行的 3 条是 `ReconnectModelTests` 里手动或靠环境变量触发的模型用例。这一轮之后产品代码又改了（审查应改 1、2），所以它只作记录，最终 head 的全量另跑。

最终 head `31b24b26` 上的 Release 全量（`full/31b24b26-summary.txt`）：

```
Passed!  - Failed:     0, Passed:  4278, Skipped:     0, Total:  4278, Duration: 24 m 18 s - ControlServer.Tests.dll (net8.0)
```

退出码 0，无 `[Test Assembly Cleanup Failure]`。TRX：total 4281、executed 4278，未执行的仍是那 3 条手动触发的模型用例。

## 与本票无关的既有不稳

`WhenAVehicleExhaustsItsBudgetTheHookIsStillCalledOnceWithOnlyTheVehiclesThatFinished` 在本机定向跑时红过（第二台车也超出 1 s 预算）。它只跑第一轮派车，那时没有旅程，本票改的循环不会进入。把产品文件换回 `fp/v2-impl` `f4f494ba` 的内容后同样连跑 5 遍，红 2 遍；修后连跑 5 遍红 4 遍。两边都是小样本，判为既有不稳（用真实计时器，cs#372 剩余风险第 11 条点过名），不在本票改。

独立审查单独起进程跑这一条，基线与本票版本都是 5/5 红，判定与本票无关，由调度另开票。7649d656 上的全量里它是绿的。
