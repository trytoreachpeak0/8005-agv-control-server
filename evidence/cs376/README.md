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
- **cs#342 模型在 bc0e3685 上重跑**（经 Invoke-HeavyLocal）：`cs342-model/report-bc0e3685.txt`，与 4cd7525b 那份相同：300/300 送达，ack 冲突 0，车辆倒退 0，`ORDER_HANG` 5 次（集成分支基线原有）。

## 真装置（CI，70737b23）

- run 36477303574（`../l2/20260929-ci-36477303574-cs376-real-rig/`）：`in-transit-door-facts` PASS；`cancelled-rebuild-cargo-proof` FAIL，转交接之后 90 秒内车载端没有出故障交接入口。服务端写库时间线与 PASS 那次同一步列级别一致；证据里没有出站内容记录，读不到就绪通知有没有发出、车载端有没有 ack。结论「服务端一侧排除，但只是推的；车载端和传输两侧分不开」（调度 2026-09-29 接受），写进 PR 剩余风险。
- run 36479121507（`../l2/20260929-ci-36479121507-cs376-real-rig/`，只重跑 cargo-proof）：FAIL 在 L2-RC-03。产品先存重建记录、再存旅程码，场景只等记录，读到两次保存之间的旧码。这次绿的那几步不用来给上一次定性。场景判据已在 459cc3e0 改成两样都等到。

两个目录只留判定用得上的文件：SUMMARY、assertions、timeline、commits.json，加上定位用到的库快照与日志（前者是车载端应用日志，后者是服务端日志 23270–23305 行摘录）。

## 增量审查之后（第三轮，最终代码 459cc3e0）

增量审查没有必修项，三条建议：S1 交接结算只按旅程收口、S2 转交接被拒不释放、S3 注明旧绑定可能在交接后仍在效。

- **修前红**：`e34a146b` 只改 tests，在 70737b23 上跑 `FaultedCargoBindingLifecycleTests` 22 条，红 1 格 `AHandoffKeepsTheBindingWhileAnotherDemandIsStillOnBoard(handed-off-demand)`：断言绑定仍在效，实际已被释放。
- **变异**（`mutations-r3/`，预期先写在 `plan.md`，过滤到 10 组相关类共 527 条）：
  - P1（审查员的：收口换成无条件释放本车全部在效绑定）：红新加的两格，与预期一致。
  - P3（审查员的：转交接判货时就释放）：红 `ARefusedRequestReleasesNothing(handoff-nothing-on-board)`，与预期一致。
  - P4（恢复按需求逐条释放）：红 handed-off-demand 那一格，与预期一致。
  - M12 重跑（删掉按旅程收口；它所在的方法这轮改了）：红 9 格，比预期少一格。`AHandedOffCargoBindingIsNotTakenForTheVehiclesNextFault` 没红，因为它断的是下一次故障，而故障协调器建绑定前会先释放别的旅程的绑定。是预期写错了，交接结算本身由另外 8 格守，详见 `plan.md`。
- **全量**：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release`，在 459cc3e0 上经 Invoke-HeavyLocal 跑，3029 通过，0 失败，退出码 0（上一轮 3026，本轮新加 3 格），日志在 `full/full-459cc3e0.log`。
