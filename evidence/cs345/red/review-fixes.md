# cs#345 独立审查（f848101c）之后的修改：红证据与反向验证

审查结论在 PR 评论 issuecomment-5800728497。每轮先提交测试、在测试提交上跑 `dotnet test --filter`（`StoppedRebuildExitTests`，入口那轮加 `VehicleFaultRecoveryEndpointsTests`）得到下面的红，再提交实现。

## 2a9063d9 审查 M1、S3（四条红）
```
  Failed ControlServer.Tests.StoppedRebuildExitTests.AFailedHandoffLeavesTheTripAWayOut [2 s]
   Assert.Equal() Failure: Values differ
Expected: Tuple (RecoveryRequired, "CARGO_HANDOFF_REQUIRED")
Actual:   Tuple (Ready, "READY")
  Failed ControlServer.Tests.StoppedRebuildExitTests.OnlyThisVehiclesBlockedJourneyAwaitingAHandoffHoldsItsSession [3 s]
Expected: Tuple (RecoveryRequired, "CARGO_HANDOFF_REQUIRED")
Actual:   Tuple (Ready, "READY")
  Failed ControlServer.Tests.StoppedRebuildExitTests.AHandoffRecordWhoseJourneyIsNoLongerBlockedAsksTheVehicleForNothing [2 s]
   Assert.False() Failure
Expected: False
Actual:   True
  Failed ControlServer.Tests.StoppedRebuildExitTests.APartialHandoffLetsThePersonGiveTheRestUp [2 s]
Expected: Tuple (Refused, "OWN_ORDER_REBUILD_EXIT_NOTHING_ON_BOARD")
Actual:   Tuple (AlreadyDone, "")
Failed!  - Failed:     4, Passed:    30, Skipped:     0, Total:    34, Duration: 39 s - ControlServer.Tests.dll (net8.0)
```
失败交接那条红在「交接失败之后就绪」那一行（它之前的断言——码被改写成 `FaultCargoRecoveryResult_NOT_RECONCILED`、绑定未了结、记录仍等交接——都通过）；部分交接那条红在「再转交接」（它之前 A 已终结、B 未终结、旅程仍 Blocked、绑定已了结、就绪 RecoveryRequired 都通过）。

## cc8b76c4 审查 S1、S5（三条红）
```
  Failed ControlServer.Tests.StoppedRebuildExitTests.AStoppedTripIsNotGivenUpUnderALatchOrAFault(state: "fault-in-effect", refusal: "FAULT_RECOVERY_FAULT_IN_EFFECT") [207 ms]
Expected: Refused
Actual:   TripTerminated
  Failed ControlServer.Tests.StoppedRebuildExitTests.AStoppedTripIsNotGivenUpUnderALatchOrAFault(state: "latched", refusal: "FAULT_RECOVERY_EMERGENCY_LATCHED") [144 ms]
Expected: Refused
Actual:   TripTerminated
  Failed ControlServer.Tests.StoppedRebuildExitTests.GivingUpARebuildStoppedBeforeItsNewOrderWasConfirmedIsRefused [125 ms]
Expected: Refused
Actual:   TripTerminated
Failed!  - Failed:     3, Passed:    35, Skipped:     0, Total:    38, Duration: 38 s - ControlServer.Tests.dll (net8.0)
```
`AHandedOverTripIsNotRebuiltByAPerson` 在测试提交上已绿：拒绝是 adbe7878（M1 的实现）引入的，判别力见反向验证。

## ab1d40b3 审查 S2、S4（七条红）
```
  Failed ControlServer.Tests.VehicleFaultRecoveryEndpointsTests.AnExitFromAStoppedRebuildIsOkOnceAndAlreadyDoneAfterwards(action: "REBUILD_STOPPED_ORDER", ...) [399 ms]
   System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.
  （TERMINATE_STOPPED_TRIP、PREPARE_CARGO_HANDOFF 两格同样）
  Failed ControlServer.Tests.StoppedRebuildExitTests.AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest(session: "ready") [204 ms]
Expected: Tuple ("ENDED", "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW", "L1-OPERATOR-345")
Actual:   Tuple ("ENDED", "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW", null)
  （session: "not-ready-on-own-order" 同样）
  Failed ControlServer.Tests.StoppedRebuildExitTests.AStoppedTripWithCargoOnBoardCanBeHandedToTheExceptionSession [2 s]
Expected: Tuple ("AWAITING_CARGO_HANDOFF", "L1-OPERATOR-345")
Actual:   Tuple ("AWAITING_CARGO_HANDOFF", null)
  Failed ControlServer.Tests.StoppedRebuildExitTests.APartialHandoffLetsThePersonGiveTheRestUp [2 s]
Expected:   (predicate expression)
Failed!  - Failed:     7, Passed:    55, Skipped:     0, Total:    62, Duration: 40 s - ControlServer.Tests.dll (net8.0)
```
S4 的缺口用例（`AForcedMechanicalRecoveryOfAHandedOverTripSettlesItsBinding`、三个出口的 `AnUnnamedOrUnconfirmedRequestIsRefusedForBoth`、入口层三个出口的 `AnExitForAJourneyThatIsNotStoppedIsAConflict`）在测试提交上即绿：它们补的是已有行为的覆盖，判别力见反向验证。

## 31474479 看板文案（两格红）
`AStoppedRebuildCardNamesTheWayOut` 新加的两格：`OWN_ORDER_REBUILD_STOPPED` 的说明里没有「默认 10 分钟」与配置项名；
`OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF` 的说明里没有交接失败后的 `PREPARE_CARGO_HANDOFF`／`TERMINATE_STOPPED_TRIP`。

## 反向验证（审查 S6）：在最终代码上把全部变异重跑一遍

`l1-mutations/mutate.py` 在 `e642f185`（产品代码与 `306fd146` 相同）上跑完整个列表，完整输出在
`l1-mutations/output-final-e642f185.txt`；`M1d2`、`M3b` 两条补在 `l1-mutations/output-final-m1d2-m3b.txt`。每条先
`dotnet build --no-incremental`（0 Error(s)）再 `dotnet test --no-build`，替换点命中次数必须等于期望（默认 1），跑完按备份还原并刷新时间戳，
收尾 `git status` 干净。**原来只存了脚本、没存输出**（审查 S6）；甲、乙两轮当时的输出已经找不到了，所以不补旧输出，改为在最终代码上全部重跑，
这份输出覆盖了全部变异。

| 变异 | 结果（红的用例） |
| --- | --- |
| M1-window-not-restarted | 2 条红： AfterAPersonsRebuildTheWindowRunsFromTheRequest、AStoppedRebuildIsRebuiltOnceOnAPersonsRequest |
| M2-not-only-stopped | 8 条红： AHandoffIsPreparedOnlyForAStoppedTripWithCargoOnBoard(state: "rebuild-waiting")、AHandoffIsPreparedOnlyForAStoppedTripWithCargoOnBoard(state: "under-way")、GivingUpAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "rebuild-waiting")、GivingUpAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "under-way")、ARequestForAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "under-way")、ARequestForAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "rebuild-waiting")、TheSameGiveUpTwiceTerminatesOnce、TheSameRequestTwiceRebuildsOnce |
| M3-no-idempotency | 跳过：替换原文在最终代码上匹配 0 次 |
| M4-old-snapshot-counts | 2 条红： AStoppedRebuildIsRebuiltOnceOnAPersonsRequest、APersonsRebuildWithCargoOnBoardStillNeedsAFreshSnapshotOfTheCargo |
| M5-delay-waited | 4 条红： AfterAPersonsRebuildTheWindowRunsFromTheRequest、AStoppedRebuildIsRebuiltOnceOnAPersonsRequest、APersonsRebuildWhileTheSessionIsNotReadyOnItsOwnOrderWaitsForTheSession、TheSameRequestTwiceRebuildsOnce |
| M6-cargo-stop-not-refused | 2 条红： GivingUpAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "cargo-not-in-place")、ARequestForAJourneyNotStoppedByTheThirdGuardIsRefusedAndChangesNothing(state: "cargo-not-in-place") |
| N1-live-binding-ignored | 跳过：替换原文在最终代码上匹配 0 次 |
| N2-memberships-ignored | 跳过：替换原文在最终代码上匹配 0 次 |
| N3-riot-not-read | 2 条红： AStoppedTripIsNotGivenUpWhileRiotMayHoldAnUnfinishedOrder(unfinished: True)、AStoppedTripIsNotGivenUpWhileRiotMayHoldAnUnfinishedOrder(unfinished: null) |
| N4-no-giveup-idempotency | 1 条红： TheSameGiveUpTwiceTerminatesOnce |
| N5-closure-not-sent | 3 条红： EveryFileThatCanCloseAJourneySendsTheClosure、AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest(session: "not-ready-on-own-order")、AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest(session: "ready") |
| H1-readiness-input-removed | 4 条红： AFailedHandoffLeavesTheTripAWayOut、ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "ready")、OnlyThisVehiclesBlockedJourneyAwaitingAHandoffHoldsItsSession、APartialHandoffLetsThePersonGiveTheRestUp |
| H2-readiness-any-blocked | 跳过：替换原文在最终代码上匹配 0 次 |
| H3-readiness-any-vehicle | 跳过：替换原文在最终代码上匹配 0 次 |
| H4-journey-not-blocked | 10 条红： AFailedHandoffLeavesTheTripAWayOut、ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "not-ready-on-own-order")、ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "ready")、AStoppedTripWithCargoOnBoardCanBeHandedToTheExceptionSession、OnlyThisVehiclesBlockedJourneyAwaitingAHandoffHoldsItsSession、AHandedOverTripIsNotRebuiltByAPerson、AHandedOffCargoBindingIsNotTakenForTheVehiclesNextFault、APartialHandoffLetsThePersonGiveTheRestUp、TheSameHandoffPreparationTwiceIsDoneOnce、AForcedMechanicalRecoveryOfAHandedOverTripSettlesItsBinding |
| H5-binding-not-released | 5 条红： ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "not-ready-on-own-order")、ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "ready")、AHandedOffCargoBindingIsNotTakenForTheVehiclesNextFault、APartialHandoffLetsThePersonGiveTheRestUp、AForcedMechanicalRecoveryOfAHandedOverTripSettlesItsBinding |
| H6-claim-not-extended | 跳过：替换原文在最终代码上匹配 0 次 |
| H7-nothing-on-board-allowed | 2 条红： AHandoffIsPreparedOnlyForAStoppedTripWithCargoOnBoard(state: "nothing-on-board")、APartialHandoffLetsThePersonGiveTheRestUp |
| N1b-live-binding-ignored | 1 条红： AStoppedTripWithCargoOnBoardIsNotGivenUp(cargo: "live-cargo-binding") |
| N2b-memberships-ignored | 2 条红： AStoppedTripWithCargoOnBoardCanBeHandedToTheExceptionSession、AStoppedTripWithCargoOnBoardIsNotGivenUp(cargo: "loaded") |
| H6a-claim-read-not-extended | 跳过：替换原文在最终代码上匹配 0 次 |
| H2r-readiness-ignores-record | 1 条红： OnlyThisVehiclesBlockedJourneyAwaitingAHandoffHoldsItsSession |
| H3r-readiness-any-vehicle | 1 条红： OnlyThisVehiclesBlockedJourneyAwaitingAHandoffHoldsItsSession |
| H2b-readiness-ignores-blocked | 1 条红： OnlyThisVehiclesBlockedJourneyAwaitingAHandoffHoldsItsSession |
| S3-claim-ignores-blocked | 1 条红： AHandoffRecordWhoseJourneyIsNoLongerBlockedAsksTheVehicleForNothing |
| H6r-claim-not-extended | 4 条红： AFailedHandoffLeavesTheTripAWayOut、ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "not-ready-on-own-order")、ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "ready")、TheSameHandoffPreparationTwiceIsDoneOnce |
| M1a-giveup-rejects-handoff-state | 2 条红： AFailedHandoffLeavesTheTripAWayOut、APartialHandoffLetsThePersonGiveTheRestUp |
| M1b-prepare-rejects-handoff-state | 3 条红： AFailedHandoffLeavesTheTripAWayOut、APartialHandoffLetsThePersonGiveTheRestUp、TheSameHandoffPreparationTwiceIsDoneOnce |
| M1d-handoff-record-not-read | 编译失败（CS8602），本轮作废 |
| M1e-rebuild-accepts-handoff-state | 2 条红： AHandedOverTripIsNotRebuiltByAPerson、TheSameHandoffPreparationTwiceIsDoneOnce |
| S1-giveup-before-confirmation | 1 条红： GivingUpARebuildStoppedBeforeItsNewOrderWasConfirmedIsRefused |
| S5a-giveup-ignores-latch | 1 条红： AStoppedTripIsNotGivenUpUnderALatchOrAFault(state: "latched") |
| S5b-giveup-ignores-fault | 1 条红： AStoppedTripIsNotGivenUpUnderALatchOrAFault(state: "fault-in-effect") |
| S2a-giveup-operator-not-kept | 2 条红： AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest(session: "not-ready-on-own-order")、AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest(session: "ready") |
| S2b-terminated-not-returned | 4 条红： AnExitFromAStoppedRebuildIsOkOnceAndAlreadyDoneAfterwards(action: "TERMINATE_STOPPED_TRIP")、AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest(session: "not-ready-on-own-order")、AStoppedTripWithNothingOnBoardIsGivenUpOnAPersonsRequest(session: "ready")、APartialHandoffLetsThePersonGiveTheRestUp |
| S2c-prepare-operator-not-kept | 1 条红： AStoppedTripWithCargoOnBoardCanBeHandedToTheExceptionSession |
| S4a-forced-mechanical-not-settled | 1 条红： AForcedMechanicalRecoveryOfAHandedOverTripSettlesItsBinding |
| S4b-giveup-ignores-person | 1 条红： AnUnnamedOrUnconfirmedRequestIsRefusedForBoth(action: TerminateStoppedTrip) |
| S4c-prepare-ignores-person | 1 条红： AnUnnamedOrUnconfirmedRequestIsRefusedForBoth(action: PrepareCargoHandoff) |
| M1d2-handoff-record-not-found | 4 条红： AFailedHandoffLeavesTheTripAWayOut、AHandedOverTripIsNotRebuiltByAPerson、APartialHandoffLetsThePersonGiveTheRestUp、TheSameHandoffPreparationTwiceIsDoneOnce |
| M3b-no-idempotency | 1 条红： TheSameRequestTwiceRebuildsOnce |

**跳过的几条为什么不算漏**：`M3`、`N1`、`N2` 的替换原文是它们当时（`4fafb17b`、`1709e2eb`）的代码，后来那段判据收进了共用的
`MayCarryAsync`、参数名改成 `trip.Runtime`，由 `N1b`、`N2b`、`M3b` 按最终代码重写；`H2`、`H3`、`H6`、`H6a` 改的是审查 M1、S3 之前的
就绪与快照请求写法，由 `H2r`、`H3r`、`H2b`、`H6r`、`S3` 取代。

**`M1d` 第一种写法作废**：把 `else if (runtime is { Stage: Blocked })` 换成恒假，编译器在 lambda 里把 `runtime` 判为可能为 null（CS8602），
没有编过；那一轮的测试是在上一状态的二进制上跑的，结果不算。`M1d2` 改成让查询本身找不到那条记录，编过、四条红。

**`M2` 的红不全是断言红**（审查 S6）：`l1-mutations/output-m2-failure-messages.txt` 单独重跑并记下了每条的失败原文——八条里五条是
`Assert.Equal() Failure`（拒绝原因或结果不对），两条（正常在途的放弃与人工重建）是 `System.NullReferenceException`，一条（放弃的重复请求）
是 `System.InvalidOperationException`（LINQ 参数求值时碰到 null）。去掉「只对停住的旅程生效」之后，正常在途的旅程没有停住记录，
后面的代码拿着 null 往下走就崩了；这仍然说明判据被守住（去掉就红），但红在崩溃上，不是在「应当拒绝却受理了」的断言上。

**为什么别的用例不红**：每条变异只动一个判据，红的都是断那个判据、或者走到那一行的用例；其余用例的路径不经过被改的那一行。
例如 `S5a` 只去掉急停检查，只有急停那一格红；`S2b` 只去掉终结清单，红的是断清单的三处（入口响应、9203、部分交接后的放弃）；
`H4`（转交接不置 Blocked）红得多，因为交接衔接的每一条用例都要先把旅程挂成 Blocked。
