# control-server#709 证据：待送点取货停靠不设倒计时（R3）

本机第 1 轮（C11 放行，2026-10-14 21:29～21:43:38 CST），被测提交 `2b25b45be`（L2 第 1、2 个目录）与 `4b6462648`（L2 第 3 个目录）。

## 定向单元测试

`dotnet test tests/ControlServer.Tests -c Release --filter ...`，范围：本票新增的 `StagingPickupNoCountdownTests`、`StagingRulesOnMachineWireParityTests`、
`StagingSwitchOffWireParityTests`，以及 `JourneyRuntimeOptionsTests`、`StationTimeoutRedispatchTests`、`Batch7CargoHolding*`、`ReversedDirection*`、
`WaitingJourney*`、`Batch7StopDrivenAdvanceWireParityTests`、`JourneyRuntimeWorkerLoadDeadlineTests`、`UnloadStopDepartureWaitTests`、
`CancelledLastEntryEndsIntoDepartureWaitTests`、`SameStation*` 与全部 `ArchitectureTests`。

```
Passed!  - Failed:     0, Passed:   340, Skipped:     0, Total:   340, Duration: 1 m 10 s - ControlServer.Tests.dll (net8.0)
```

退出码 0（出站报文的 schema 检查在收尾时报，看退出码，不只看汇总行）。

**红过一次，留档**：第一轮 `ADoorLeftOpenAtTheStagingStationIsReportedByTheTimeSinceTheLoadCommand` 三种停靠全红，
`Expected: "STATION_TIMEOUT_DOOR_NOT_CLOSED"` / `Actual: null`。原因在用例：造「门没关」时写了 `SLOT_DOOR_NOT_CLOSED`，而引擎只认
`LOCK_NOT_CLOSED`（`SlotDoorsNotProvenClosed`）；未改动的机台路径一起红，说明不是产品问题。改正后绿（提交 `4b6462648`）。

## 开关关着时逐字节不变（WirePin）

- `tests/ControlServer.Tests/WirePins/staging-switch-off-station-deadline.txt`、`staging-switch-off-cargo-holding.txt`：在集成分支
  `fp/v2-impl@1f9e1b0ab` 的 detached worktree 里只放入 `StagingSwitchOffWireParityTests.cs`、产品代码一行未动跑出的 `.actual`，改名搬回本票分支；
  本票分支上两条都逐字节对上（2/2）。两份基线里离站期限与持货期限都照常下发（`stationDepartureDeadlineAt`、`cargoHoldingDeadlineAt` 有值）。
- 开关打开时的机台：`Batch7StopDrivenAdvanceWireParityTests` 末尾两条用集成分支上原有的 `normal-journey`、`station-deadline` 两份基线，一份未重录，逐字对上。

## L2 合成场景

| 目录 | 场景 | 结论 |
| --- | --- | --- |
| `l2/staging-pickup-no-station-timeout-001` | 待送点无人扫码、离站期限 10 秒、等满三倍以上 | PASS（4 条断言） |
| `l2/station-timeout-redispatch-to-alarm-001` | 机台回归，开关打开 | PASS；证据里看不出开关取值 |
| `l2/station-timeout-redispatch-to-alarm-002` | 同上，编排器补记开关之后重跑 | PASS；`timeline.jsonl` 有 `StagingDepartureRules__Enabled = True` |

只保留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`；组件日志与快照未入库。

## 第 2 轮（C11 放行，2026-10-14 21:51～22:01:08 CST）

### 变异前的绿基线，以及这一轮查出的一个测试缺陷

先编译、重跑第 1 轮那组定向测试（加上两条 WirePin，共 342 条）：红 1 条，
`StagingRulesOnMachineWireParityTests.AMachineStopEndedByItsStationDeadlineSendsTheSameLinesWithTheStagingRulesOn`。单跑 WirePin 那几个类看到原文：

```
System.IO.IOException : The process cannot access the file '...\WirePins\station-deadline.txt.actual' because it is being used by another process.
```

原因在测试：本票新类与既有的 `Batch7StopDrivenAdvanceWireParityTests` 用同一份基线名，各写同一个 `.actual`，分在两个类里被 xunit 并行跑就撞上；
第 1 轮碰巧没撞。改法：两条并入既有类（同一类里串行），并与该类一样不挂切片标签（该类整类登记在 `IntegrationSliceTraitArchitectureTests` 的账本里，
混挂标签那条架构测试红过一次）。之后 `WireParityTests`、`StagingPickupNoCountdownTests`、全部 `ArchitectureTests` 共 109/0，退出码 0（本 README 所在提交之前的两次提交）。

### 变异（只改 `src/ControlServer.Host/Runtime/StagingPickupStops.cs`，每轮跑完 `git checkout` 这一个文件，工作树 0 行改动）

范围：`StagingPickupNoCountdownTests` 与全部 `*WireParityTests`，28 条。

| 变异 | 改法 | 结果 | 红的用例 |
| --- | --- | --- | --- |
| 判别恒假 | 取货角色判断后 `|| true` 直接答否 | 6 红 / 22 绿 | `StagingPickupNoCountdownTests` 六条用例的 `StagingRulesOn` 一格：期限下发（验收 1）、一小时无人扫码（验收 2）、装一篮一小时（验收 3）、仓门告警（验收 4）、缺电放回（验收 5）、看板（验收 7） |
| 判别恒真 | 任何取货停靠都答是 | 8 红 / 20 绿 | 两条开关打开时的机台 WirePin（`normal-journey`、`station-deadline`），以及 `StagingPickupNoCountdownTests` 六条用例的 `MachineRulesOn` 一格（验收 6 的机台回归） |

`StagingRulesOff` 一格两次都绿：判别在开关之后，开关关着时变异碰不到行为——这正是「开关关着逐字节不变」要的。
