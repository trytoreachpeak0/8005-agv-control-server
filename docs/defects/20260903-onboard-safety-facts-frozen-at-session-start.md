# 缺陷：车载安全事实冻结在会话建立那一刻，阻塞原因随后又被覆盖

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: [`2026-09-03 现场联调复盘`](https://github.com/trytoreachpeak0/8005-agv-program/blob/main/docs/wire-to-gate-test-automation.md)
Product at discovery: `ControlServer_MVP@1c67367372617ae6f0b985b6a11b65c53b16f846`
Peers: `OnboardHmi_MVP@238b46eb2c9ae90584e4288a782176f66b7de942`、`protocol-v0.1.1@1531489`

2026-09-03 的现场联调跑通了「真实需求受理 → 建真实 RIoT 单 → 真车开到取货点 → 条码输入 →
下发装载指令 → IO 闭环」，止于装载环节的一次操作超时。挖出的三个缺陷里，两个在本仓库，都在
`JourneyRuntimeEngine` 里，都与「一个事实在会话生命周期内如何保持当前」有关。

---

## D-1：`ReadOnboardFactsAsync` 只认 `SafetyStateSnapshot`，读不到后续的变化通知

### 现象

车载端每个会话只发一次 `SafetyStateSnapshot`，之后的每次变化都走 `SafetyStateChanged`
（ADR-cross-0033）。服务端两条路径消费的数据源却不同：

- `SessionRecoveries.DepartureSafe` 由 `OnboardMessageProcessor` 的 `SafetyStateChanged`
  分支更新，所以会话就绪状态是正确的、会随车辆状态翻转。
- `ReadOnboardFactsAsync` 读的是
  `LatestInboxForSessionAsync("SafetyStateSnapshot", ...)`，取到的 `safety` 块永远停留在
  会话建立那一刻。

于是安全摘要里的 `vehicleStopped` / `allTargetSlotsLocked` / `allUnlockOutputsReset` /
`unknownPresent` 四项在整个会话期间都是常量——它们不像 `departureSafe` 那样在会话行上另有
副本。

两个方向都会出事：

- **会话在车辆运动中建立**：快照记下 `vehicleStopped=false`。车开到取货点停稳后，
  `SafetyStateChanged` 让会话回到 `Ready`，但 `IsTrustedArrivalAsync` 的
  `onboard.VehicleStopped` 仍为假，journey 卡死在 `AwaitingPickupArrival`；同样地
  `ValidateDynamicFacts` 一直返回 `ONBOARD_DEPARTURE_UNSAFE`，任何需求都受理不了。只能
  重启车载端换一个会话代次。**每一趟取货都会命中**，因为会话建立后车必然会动。
- **会话在车辆静止时建立**（更危险的方向）：快照记下 `vehicleStopped=true`。此后车载端报告
  车辆运动，服务端仍然认为它停着，`IsTrustedArrivalAsync` 会采信一次并不成立的到站。

### 为什么归属在服务端

车载端的发送行为与协议一致：`SafetyStateSnapshot` 与 `SafetyStateChanged` 携带同一个
`SafetySummary`，`safetyStateVersion` 单调递增，服务端在同一个事务里既存入站信封又推进
`SessionRecoveries.SafetyRevision`。是服务端单方面只读了其中一种消息。

车载端另有一个相关的语义问题（车辆停稳后是否应重报快照）已在
[`8005-agv-onboard-hmi#2`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/2)
里请对方定夺；**我方这半边不依赖对方排期**，因为服务端本来就该读它已经收到的那条消息。

### 修复

安全摘要改从「携带会话当前 `safetyStateVersion` 的那条消息」读取，两种消息类型一并纳入
（`LatestSafetySummaryForSessionAsync`）。按 revision 匹配而不是按接收顺序取最新，是因为
会话行上的 revision 与信封在同一事务里推进，匹配不上就说明两者不一致，此时失败关闭。

**仓位可用性不变，仍取自 `SafetyStateSnapshot` 的 `slotStates`。**`SafetyStateChanged` 只
给出 `affectedSlots`，不给新状态，把它当作「可用性丢失」会让第一趟用过的仓位在整个会话余下
时间里全部搁浅——一次装载和它对应的卸载都会「affect」同一批仓位。会话基线正是仓位预留账本
所建立的基础。

回归测试：`AdmissionSafetyFactsComeFromTheLatestChangeNotTheSessionSnapshot`、
`ArrivalIsNotTrustedWhileTheLatestSafetyStateSaysTheVehicleIsMoving`。

---

## D-2：`Blocked` 的阻塞原因被 `ONBOARD_SESSION_NOT_READY` 覆盖

### 现象

`AdvanceAsync` 开头在没有 `Ready` 会话时无条件写入：

```csharp
runtime.BlockReasonCode = "ONBOARD_SESSION_NOT_READY";
```

一条已经因 `LOAD_RESULT_REQUIRES_RECOVERY` 进入 `Blocked` 的 journey，正常流程就是等人到车
前处置，而处置期间车载端通常是关掉的——于是下一个轮询周期就把原因抹成
`ONBOARD_SESSION_NOT_READY`。现场留下的正是一条读作
`Blocked / ONBOARD_SESSION_NOT_READY` 的记录，两个词都没有说明它在等哪一种恢复。原因没有
任何地方可以重建。

### 修复

`Stage == Blocked` 时不再覆盖 `BlockReasonCode`，也不推进 `UpdatedAt`（什么都没变）。会话
就绪状态本来就在 `SessionRecoveries` 上有自己的行和自己的原因码，`/api/runtime/sessions`
可读。

回归测试：`ABlockedJourneyKeepsTheReasonItWasBlockedForWhenTheSessionDrops`。

---

## 未修：`Blocked` 的出口依赖一个车载端从不发送的消息

交接材料把这一条描述为「`Blocked` 是终态且永久占用 active 位」，并建议「让它不再算 active」。
查证后**没有按这个方向改**，理由如下。

**服务端的出口是完整的，而且有测试。**`ExceptionRecoverySessionRequested` →
`RecoveryActionSubmitted(RESUME_AFTER_REPAIR)` → `SlotOperationResumeCommand` → 重发
`OperationResult` → `ObserveOperationResultAsync` 把 journey 放回 `AwaitingLoadResult`；
或者 `LoadCancellationResult` / `LoadCompensationResult` / `FaultCargoRecoveryResult` 走到
`Completed` 并释放 dispatch lease。见 `RecoveryStateMachineG2Tests`。

**真正的断点在车载端。**`8005-agv-onboard-hmi` 从不发送这五条出站消息中的任何一条——
`ExceptionRecoverySessionRequested` 等只出现在 `WireToGateSessionClient.cs` 的**入站**解析
分支里。现场那条 journey 之所以没有出口，是这个原因，不是服务端把 `Blocked` 当成了终态。
已开 [`8005-agv-onboard-hmi#4`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/4)。

**「让 `Blocked` 不算 active」是不安全的。**`ExecuteOnceAsync` 在 `active.Length == 0` 时会
走 `DiscoverAndAcceptAsync`；被阻塞的 demand 有自己的 runtime 行，躲得过孤儿检查。结果就是
在仓位物理状态未经证实、dispatch lease 仍被持有的情况下，把车派去执行另一条需求——这正是
ADR-cross-0006 与 ADR-cross-0015 要求人工介入所防的事。这条意图已由
`ABlockedJourneyKeepsTheVehicleOutOfEveryOtherDemand` 钉住，避免以后被悄悄改掉。

## 影响

D-1 之前，会话一旦在车辆运动中建立就再也无法受理或采信到站，只能重启车载端；反方向上服务端
可能采信一次不成立的到站。D-2 之前，阻塞诊断在车载端关机后即丢失。两条都不影响已经写入的
业务身份，`W2G-IS-00`～`07` 的既有 G2/G3 证据不因此作废（协议内容未变）。
