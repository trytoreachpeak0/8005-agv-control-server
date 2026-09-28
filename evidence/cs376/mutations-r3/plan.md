# cs#376 增量审查之后的变异（第三轮）预期

写于跑之前。代码基准：e34a146b 之上的工作树（交接结算去掉按需求逐条释放、只按旅程收口；转交接那处加注释）。

过滤：第二轮的 8 组，外加 `JourneyRuntimeWorkerCargoRecoveryTests`、`RecoveryStateMachineG2Tests`（这两类会走到交接结算）。未变异时 527 条全绿。

| # | 位置 | 替换 | 预期红 |
| --- | --- | --- | --- |
| P1 | OnboardRecoveryCoordinator.SettleHandedOffCargoAsync | 按旅程收口换成「无条件释放本车全部在效绑定」（审查员的 P1） | `AHandoffKeepsTheBindingWhileAnotherDemandIsStillOnBoard` 两格（handed-off-demand、ended-anchor），只这两格 |
| P3 | VehicleFaultRecoveryService.PrepareCargoHandoffAsync | 判货（MayCarryAsync）之前先释放别的旅程的绑定（审查员的 P3） | `ARefusedRequestReleasesNothing(handoff-nothing-on-board)`，只这一格 |
| P4 | OnboardRecoveryCoordinator.SettleHandedOffCargoAsync | 恢复修之前那段按需求逐条释放的循环 | `AHandoffKeepsTheBindingWhileAnotherDemandIsStillOnBoard(handed-off-demand)`，只这一格 |

## M12 重跑（第二轮 M12 的替换文本不变，但它所在的方法这轮改了）

去掉逐条释放之后，删掉按旅程收口就等于交接结算什么都不释放。预期红（写于跑之前）：

- `FaultedCargoBindingLifecycleTests.AHandoffReleasesABindingNamingAnEndedDemandOfTheOpenJourney` 两格（第二轮 M12 就红这两格）
- `FaultedCargoBindingLifecycleTests.AnOnBoardDemandWithNoRecordedSlotsStopsTheRebuildForAPerson`（交接走到底后断言没有在效绑定）
- `FaultedCargoBindingLifecycleTests.ARealCargoOnASecondTripKeepsItsProtectionBesideAStaleBinding`（交接走到底，不确定它是否断言释放）
- `StoppedRebuildExitTests` 里断言交接释放的五个：`ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere`（全部格）、`AForcedMechanicalRecoveryOfAHandedOverTripSettlesItsBinding`、`AFailedHandoffLeavesTheTripAWayOut`、`APartialHandoffLetsThePersonGiveTheRestUp`、`AHandedOffCargoBindingIsNotTakenForTheVehiclesNextFault`

不该红：`AHandoffKeepsTheBindingWhileAnotherDemandIsStillOnBoard` 两格（它们断言不释放）。
第二轮 M12 时这五个 #345 用例没红，因为当时逐条释放那段还在、能单独放掉被交接需求名下的绑定。

## 结果与预期的出入（跑完之后补）

P1、P3、P4 与预期逐个一致。M12 红 9 格，比预期少一格：`StoppedRebuildExitTests.AHandedOffCargoBindingIsNotTakenForTheVehiclesNextFault` 没红。
原因：这一格不直接断交接结算，而是断「下一次故障不把车当有货」。故障协调器在建绑定前先释放别的旅程留下的绑定（本票第二层，
第二轮 M3 守它），所以交接什么都不释放时，下一次故障仍然看到空车。是我的预期写错了，不是漏守：交接结算本身由另外 8 格守。

P4 用的是 `runtime.DemandId`（旅程锚需求），修之前的循环用的是被交接需求。这两个用例里被交接的正好是锚需求，两者等价。
