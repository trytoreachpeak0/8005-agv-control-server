# cs#345 L1 红证据与反向验证

每轮先提交测试、在测试提交上跑 `dotnet test --filter` 得到下面的红，再提交实现。反向验证用同目录的 `l1-mutations/mutate.py`。

## 960c7fc8 AStoppedRebuildIsRebuiltOnceOnAPersonsRequest
```
  Failed ControlServer.Tests.StoppedRebuildExitTests.AStoppedRebuildIsRebuiltOnceOnAPersonsRequest [9 s]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: Tuple (RebuildRequested, "REBUILD_SCHEDULED")
Actual:   Tuple (Refused, "NONE")
```

## cae9a578 负例与重复请求
```
  Failed ControlServer.Tests.StoppedRebuildExitTests.ARequestForAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "vehicle-ineligible", refusal: "OWN_ORDER_REBUILD_EXIT_VEHICLE_INELIGIBLE") [398 ms]
Expected: Refused
Actual:   RebuildRequested
  Failed ControlServer.Tests.StoppedRebuildExitTests.ARequestForAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "cargo-not-in-place", refusal: "OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE") [3 s]
Expected: Refused
Actual:   RebuildRequested
  Failed ControlServer.Tests.StoppedRebuildExitTests.TheSameRequestTwiceRebuildsOnce [501 ms]
Expected: Tuple (AlreadyDone, 0)
Actual:   Tuple (Refused, 1)
Failed!  - Failed:     3, Passed:     4, Skipped:     0, Total:     7
```

## 甲的反向验证（在 4fafb17b 上，mutate.py，每条先 build --no-incremental、0 Error(s)，再 test --no-build）
| 变异 | 红的用例 |
| --- | --- |
| M1 原行重开时不改写 IncidentAt（窗口不重算） | AfterAPersonsRebuildTheWindowRunsFromTheRequest、AStoppedRebuildIsRebuiltOnceOnAPersonsRequest |
| M2 去掉「只对停住的旅程生效」（非停住分支不拒） | 负例 under-way、rebuild-waiting；TheSameRequestTwiceRebuildsOnce |
| M3 去掉幂等判断 | TheSameRequestTwiceRebuildsOnce |
| M4 重开时不改写 RecordedAt（旧快照算数） | APersonsRebuildWithCargoOnBoardStillNeedsAFreshSnapshotOfTheCargo、AStoppedRebuildIsRebuiltOnceOnAPersonsRequest |
| M5 人工重建也等延迟 | 窗口、首条、会话未就绪、重复请求四条 |
| M6 货不在原仓停住不拒 | 负例 cargo-not-in-place |

## dda75a39 乙（十条全红，均为 Actual "FAULT_RECOVERY_ACTION_UNKNOWN" 或 Refused）
```
  Failed ...AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest
Expected: Tuple (TripTerminated, "TRIP_TERMINATED")
Actual:   Tuple (Refused, "NONE")
  Failed ...AStoppedTripWithCargoOnBoardIsNotGivenUp(cargo: "live-cargo-binding")
Expected: "OWN_ORDER_REBUILD_EXIT_CARGO_ON_BOARD"
Actual:   "FAULT_RECOVERY_ACTION_UNKNOWN"
  Failed ...AStoppedTripIsNotGivenUpWhileRiotMayHoldAnUnfinishedOrder(unfinished: True, refusal: "FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED")
Expected: "FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED"
Actual:   "FAULT_RECOVERY_ACTION_UNKNOWN"
  Failed ...TheSameGiveUpTwiceTerminatesOnce
Expected: TripTerminated
Actual:   Refused
Failed!  - Failed:    10, Passed:    11, Skipped:     0, Total:    21
```

## 乙的反向验证（在 1709e2eb 上）
| 变异 | 红的用例 |
| --- | --- |
| M2（共用判据）去掉「只对停住的旅程生效」 | 甲、乙两组负例的 under-way、rebuild-waiting；两条重复请求 |
| N1 忽略活着的故障货物绑定 | AStoppedTripWithCargoOnBoardIsNotGivenUp(live-cargo-binding) |
| N2 忽略归属里的已装需求 | AStoppedTripWithCargoOnBoardIsNotGivenUp(loaded) |
| N3 不看 RIoT 未终结单 | AStoppedTripIsNotGivenUpWhileRiotMayHoldAnUnfinishedOrder 两格 |
| N4 去掉放弃的幂等判断 | TheSameGiveUpTwiceTerminatesOnce |
| N5 提交后不发收尾快照（`_ = publisher;`，直接删会因 CS9113 编译失败） | EveryFileThatCanCloseAJourneySendsTheClosure、AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest |

## 741babe0 交接衔接（十条红）
```
  Failed ...ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "ready")
Expected: Tuple (HandoffPrepared, "AWAITING_CARGO_HANDOFF")
Actual:   Tuple (Refused, "NONE")
  Failed ...AHandedOffCargoBindingIsNotTakenForTheVehiclesNextFault
Expected: "ExceptionRecoverySessionOpened"
Actual:   "ExceptionRecoverySessionRejected"
  Failed ...OnlyTheHandoffCodeOnThisVehiclesJourneyHoldsItsSession
Expected: HandoffPrepared
Actual:   Refused
Failed!  - Failed:    10, Passed:    21, Skipped:     0, Total:    31
```
（第一条在转交接那一步之前的断言全部通过：修前会话被拒 RECOVERY_DEMAND_NOT_BLOCKED、就绪 READY。）

## 交接衔接的反向验证（在 23ce5a7c 上；H5、H6a、N1b、N2b 在补断 ReleasedAt 之后，即 c07e288a 的内容上，mutate.py）
| 变异 | 红的用例 |
| --- | --- |
| H1 就绪去掉这一项（调度条件 3） | ACargoNotInPlace…EndsThere(ready)（修前就绪是 READY、车载端不出入口）、OnlyTheHandoffCodeOnThisVehiclesJourneyHoldsItsSession |
| H2 就绪认任何 Blocked 旅程 | OnlyTheHandoffCode…（SOME_OTHER_BLOCK 那一步） |
| H3 就绪不分车 | OnlyTheHandoffCode…（AGV-OTHER 那一步） |
| H4 转交接不把旅程置 Blocked | 两格首条交接、有货护栏三转交接、重复转交接、交接后下一次故障、OnlyTheHandoffCode |
| H5 交接后不写 ReleasedAt | 首条交接两格（补断 ReleasedAt 之后）、AHandedOffCargoBindingIsNotTakenForTheVehiclesNextFault |
| H6a 快照请求不认 AWAITING_CARGO_HANDOFF | 首条交接两格、TheSameHandoffPreparationTwiceIsDoneOnce |
| H7 没货也允许转交接 | AHandoffIsPreparedOnly…(nothing-on-board) |
| N1b 共用判据忽略活着的绑定 | AStoppedTripWithCargoOnBoardIsNotGivenUp(live-cargo-binding) |
| N2b 共用判据忽略已装归属 | AStoppedTripWithCargoOnBoardIsNotGivenUp(loaded)、AStoppedTripWithCargoOnBoardCanBeHandedToTheExceptionSession |
第一次跑 H5 只断了 ReleasedReason 没红，补断 ReleasedAt 后红。

## d91816a1 入口（四条红）
```
  Failed ...VehicleFaultRecoveryEndpointsTests.AnExitFromAStoppedRebuildIsOkOnceAndAlreadyDoneAfterwards(action: "REBUILD_STOPPED_ORDER", ...)
Expected: typeof(Microsoft.AspNetCore.Http.HttpResults.Ok<VehicleFaultRecoveryResponse>)
Actual:   typeof(Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)
  Failed ...VehicleFaultRecoveryEndpointsTests.AnExitForAJourneyThatIsNotStoppedIsAConflict
Expected: 409
Actual:   422
Failed!  - Failed:     4, Passed:    14, Skipped:     0, Total:    18
```

## e7675444 看板（四条红）
`AStoppedRebuildCardNamesTheWayOut` 三格（说明里没有出口动作名、MES 后果、recoveryResumeEnabled）与
`AStalledInTransitOrderIsShownWithAChineseDescription(OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF)`（`... has no description`）。

## 关于 `l1-mutations/mutate.py`
变异表按跑的先后累加，每条的替换原文对应它跑时的代码：N1、N2 对应 1709e2eb（乙那时自己判车上有货），
23ce5a7c 起那段判据收进共用的 `MayCarryAsync`，所以在最终代码上 N1、N2 匹配 0 次，由 N1b、N2b 取代。
