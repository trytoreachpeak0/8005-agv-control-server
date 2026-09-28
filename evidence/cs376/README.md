# cs#376 证据

故障货物绑定（`FaultedVehicleCargo`）在货离车时释放，新故障只认本趟旅程的货，重建不再停在没有出口的等待里。

## 第一步核实（改代码之前，基准 2419ecfd）

探针的结论已经转成正式用例（`FaultedCargoBindingLifecycleTests`），这里只记机器查找的原始计数。

- `machine-find/hits-summary.txt`：在 `ControlServerDbContext` 的两个 `SaveChanges` 里临时注入记录（只记、不抛异常），在 2419ecfd 加探针类上跑全量 3011 条。计数三类事件：新建绑定、释放绑定（带释放原因）、旅程转 Completed 时车上还有在效绑定（LEFT）。
  - 释放只见到三种原因：`RESUMED_ON_ORIGINAL_VEHICLE`、`REBUILT_ON_ORIGINAL_VEHICLE`、`HANDED_OFF_IN_EXCEPTION_SESSION`。
  - LEFT 除一条靠改库造出状态的产品用例以外，全部来自探针：cs#335 的入口，也就是单在 RIoT 被取消后重建，再卸货收尾。
  - 注入已从备份还原，`git diff` 为空。
- 推断 a（清除有货后重建出来的单，在确认前被取消）已实测会留下绑定，现为用例 `AnUnloadAfterARebuildCancelledBeforeConfirmationReleasesTheBinding`。
- 推断 b（多需求旅程里绑定记的是锚需求，交接只释放会话需求那一条）没能在夹具里造出「锚需求先终结、另一条装车」的形状，见 PR 剩余风险。

## 修前红

- `fabacd97`：10 条，只改 tests，在 2419ecfd 的产品代码上全部红。
- `dfc5ea76`：追加 2 条（续行入口、清除入口的旧库状态）。在一个 detached worktree 里检出这个提交（其 src 与 2419ecfd 相同）跑，12 条全红。

## 变异（`mutations/`）

先写预期（`plan.md`），再逐个替换：断言恰好命中 1 处，`dotnet build --no-incremental`，然后 `dotnet test --no-build`，过滤到故障与重建相关的 8 个类，共 395 条。结束后从备份还原并 touch 源文件。

| # | 行 | 退出码 | 红的用例 | 与预期 |
| --- | --- | --- | --- | --- |
| M1 | JourneyRuntimeEngine.cs:1503 | 1 | 卸货释放 4 格 | 一致 |
| M2 | FaultedCargoBindings.cs:65 | 1 | TheFirstOfTwoUnloadsKeepsTheBindingAndTheLastReleasesIt | 一致 |
| M3 | VehicleFaultCoordinator.cs:680 | 1 | ALoadedSecondTrip…、ARealCargoOnASecondTrip… | 一致 |
| M4 | VehicleFaultRecoveryService.cs:407 | 1 | AResumptionEntryReleases… | 一致 |
| M5 | VehicleFaultRecoveryService.cs:602 | 1 | AClearanceReleases… | 一致 |
| M6 | VehicleFaultRecoveryService.StoppedRebuild.cs:353 | 1 | AnEmptySecondTripStoppedByTheThirdGuardCanBeGivenUp | 一致 |
| M7 | JourneyRuntimeEngine.OwnOrderRebuild.cs:510 | 1 | ARebuildLeftWaitingOnAnEmptySetOfSlotsGoesOnWithoutAPerson | 一致 |
| M8 | JourneyRuntimeEngine.OwnOrderRebuild.cs:783 | 1 | AnOnBoardDemandWithNoRecordedSlotsStopsTheRebuildForAPerson | 一致 |
| M9 | FaultedCargoBindings.cs:118 | 1 | ABindingNoJourneyAccountsForIsKept、VehicleFaultIsolationTests.ReObservingTheSameFaultDoesNotBindTheCargoTwice | 一致 |
| M10 | FaultedCargoBindings.cs:118 | 1 | ABindingOfADemandStillInAnOpenJourneyIsKeptEvenIfAClosedJourneyOnceHadIt | 一致 |

M2 第一次写成 `if (false)`，被编译器以 CS0162（代码不可达）拒绝，没有跑成；改写成 `if (memberships.Count < 0)` 后重跑，`M2.txt` 是重跑的记录。

## 全量、模型、L2（最终提交 4cd7525b，均经 Invoke-HeavyLocal）

- 全量 `dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release`：3020 通过，0 失败，退出码 0。
- cs#342 模型测试：`ReconnectModelTests.PrototypeMeasurement`，`CS342_ITER=300`，Release，退出码 0。报告在 `cs342-model/`。300/300 送达，ack 冲突 0，车辆倒退 0；`ORDER_HANG` 5 次与 cs#366 在集成分支上的报告相同，是原有的。
- L2 `in-transit-order-cancelled-rebuilt`（合成对端）：PASS，证据在 `evidence/l2/cs376-in-transit-order-cancelled-rebuilt-4cd7525b/`，只留 SUMMARY、assertions、timeline。

## 审查之后（第二轮，最终代码 bc0e3685）

审查结论为修后可合：必修 1 条（M1，交接结算要按旅程收口），另有 9 条建议。

- **修前红**：`b274387e` 只改 tests，在 4cd7525b 上红 5 格。M1 的形状照审查员的探针 rv379a，另有被拒请求不写库两格、不向空车要快照一格。
- **变异**（`mutations-r2/`）：先写预期（`plan.md`），16 个逐个跑，红的用例与预期逐个一致。每份记录都带替换前后的原文、命中数与行号、构建与测试的退出码。上面第一轮的记录没有原文，而且对应代码之后改过，只作历史保留，以第二轮为准。
- **全量**：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release`，经 Invoke-HeavyLocal 跑，3026 通过，0 失败，退出码 0，运行日志在 `full/full-bc0e3685.log`。
- **cs#342 模型与 L2**：没有重跑，仍是 4cd7525b 上的结果。两者覆盖的是断线重连与取消后重建的整条链；本轮只改了释放点与入口判拒，不涉及这两块。
