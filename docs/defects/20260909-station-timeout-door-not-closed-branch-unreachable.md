# 缺陷：决策 4 的告警在两格里只有一格该出现——诊断本身错了一半，真 bug 在别处

Status: resolved（2026-09-09）
Owner repository: `8005-agv-control-server`
Found by: [`real-onboard-station-timeout-door-open` L2 红证据](../../evidence/l2/20260909-real-onboard-station-timeout-door-open-001/SUMMARY.md)
Product at discovery: `ControlServer_MVP@0f6b42458ab55cde4c9123ad344ddc86c3fe5774`
Peers: `OnboardHmi_MVP@98df671c846c7c572fd40d38fe16e5e084686603`、`slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`
Resolved in: [`8005-agv-program#25`](https://github.com/trytoreachpeak0/8005-agv-program/issues/25)

> **本文档 2026-09-09 重写过。**初版的结论是「决策 4 的告警分支在真装置上到不了，车停在恢复态」，
> 并给出三条候选修法。**那个结论只对一半，据它去实现任何一条修法都会改错东西。**下面第一节是重写
> 后的结论，原诊断的错处集中记在「初版错在哪里」一节，不删——它错的方式值得留着。

## 结论

决策 4 有**两格**，它们的正确行为不一样，而初版把两格当成了一格：

| 旅程 stage | 门为什么开着 | 会话 readiness | 旅程 `BlockReasonCode` | 对不对 |
| --- | --- | --- | --- | --- |
| `AwaitingLoadResult` | 我方刚下的仓位命令开的 | `Ready` / `READY` | `STATION_TIMEOUT_DOOR_NOT_CLOSED` | ✅ 决策 4 要的就是这格 |
| `AwaitingSublot` | **没有任何我方命令能解释** | `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY` | `ONBOARD_SESSION_NOT_READY` | ✅ 也是对的 |

**ADR-cross-0058 点名的正是第一格。**它的 Consequences 原文写着「服务端改动落在
`WireToGateStore.ApplyOperationResultAsync` 的 `completedSafely` 分支与 `JourneyRuntimeEngine` 的
`AwaitingLoadResult`」。`TryTimeOutSublotWaitAsync` 里那半（`AwaitingSublot`）是实现时自己加的，
ADR 从未要求过——而它加在了一个物理上不该由它管的格子里。

第二格不是缺陷，是正确行为。`AwaitingSublot` 的定义就是本站一条仓位命令都没发过，此时读到一扇开着
的门，意味着这扇门是**我方没让它开**的：机械锁没咬合、光幕极性、上一轮残留、有人手扒。这不是
ADR-cross-0040 要保护的「操作员迟疑」，这是离站安全没有人为之负责，会话降级完全正确。

## 实测

把 `JourneyRuntimeWorkerTests` 的夹具 `ReportDoorLeftOpenAsync`（直写 `SessionRecoveryRow`）换成
真实入口 `ApplySafetySnapshotAsync` + `DecideReadinessAsync`，两个 stage 各跑一次：

```
MEASURE AwaitingSublot:     prepared=0 readiness=RecoveryRequired/DEPARTURE_SAFETY_NOT_READY stage=AwaitingSublot     block=ONBOARD_SESSION_NOT_READY
MEASURE AwaitingLoadResult: prepared=1 readiness=Ready/READY                                 stage=AwaitingLoadResult block=STATION_TIMEOUT_DOOR_NOT_CLOSED
```

分水岭是 `prepared` 那一列。`IsUnsafetyExplainedByOwnCommandAsync` 的最后一个条件要求存在
`Prepared` 的 station operation，而在途仓位操作**只存在于 `AwaitingLoadResult`**——所以第一格天然
拿得到豁免，第二格天然拿不到。这两条测量现已固化成两条会响的测试，见「留下的防线」。

## 初版错在哪里

三处，都源自同一个动作：**只观测了 `AwaitingSublot` 那条路，就把结论推广到了整个决策 4。**

1. **「那段代码一次都没被执行到」**——`AwaitingSublot` 那半确实没有，`AwaitingLoadResult` 那半
   （#24 的 `ReconcileStationTimeoutDoorNotClosedAsync`，`d36f11b`）实测生效。而后者才是 ADR 点名的。
2. **「车停在恢复态，正是 ADR 明说不要的那一种」**——ADR 不要的是「把操作员迟疑升级成恢复」。
   `AwaitingSublot` 阶段一扇无人负责的门不是迟疑，把它判成需要人处理是对的。
3. **三条候选修法里有两条解决不了它们声称要解决的东西。**readiness 由
   `WireToGateStore.DecideReadinessAsync` 写，`JourneyRuntimeEngine.AdvanceAsync` 一行都不碰它。
   所以「把判定挪到就绪门之前」（修法 1）与「增设更具体的原因码」（修法 3）都只换掉旅程的
   `BlockReasonCode`，会话照样是 `RecoveryRequired`，HMI 恢复入口照样具备显示条件。能动那一条
   代价的只有修法 2，而修法 2 现已判定为不该做。

## 真正修掉的 bug：豁免查询是全表的

查这件事时撞见的，与初版诊断无关：

```csharp
// 修之前
return await dbContext.StationOperations
    .AnyAsync(item => item.Status == StationOperationStatus.Prepared, cancellationToken);
```

**没有按 agvId 过滤。**同一个 `DecideReadinessAsync` 里的 `operationNeedsRecovery` 反而老老实实
join 到了 `runtime.AgvId`。于是只要车队里**任意一台**车有在途仓位操作，其余每一台车的虚掩门都能拿到
这条豁免，读出 `Ready`。单车部署永远看不到；**车队是三台**（见 `remote-ops/fleet.md`）。

现已改成与 `operationNeedsRecovery` 同一条 join（`StationOperations` → `JourneyDemands` →
`JourneyRuntimes.AgvId`）。

## `ONBOARD_SESSION_NOT_READY` 的下一跳

这个码本身不打算改——L2 断言、`real-onboard-station-timeout-door-open` 的红证据与切片看板都钉着这个
字面量。要记住的是它的读法：**它永远等于「详见 `SessionRecoveries`」**。会话不 Ready 的具体原因
总是写在那一行，这不是巧合而是 `GetRecoveryReason` 的职责：

| 想知道 | 读哪里 | 虚掩门那一格的值 |
| --- | --- | --- |
| 是哪一类不就绪 | `SessionRecoveries.ReasonCode` | `DEPARTURE_SAFETY_NOT_READY` |
| 是不是门的问题 | `SessionRecoveries.SafetyReasonCodesJson` | `["LOCK_NOT_CLOSED"]` |
| 证据全不全 | `SessionRecoveries.SafetyUnknownPresent` | `0` |

**现场规程挂在这三个字段上，不要去找一个专门的告警码。**决策 4 那个告警码只属于
`AwaitingLoadResult` 那一格。

## 留下的防线

初版的红证据能拖到 L2 才被发现，根因是**四条门相关的 L1 测试全部用夹具直写会话行**，绕过了
`DecideReadinessAsync`——判定跌回去仍然全绿。现在多了三条会响的：

- `AnOpenDoorNoCommandOfOursExplainsLeavesTheSessionUnusable`——`AwaitingSublot` 走真实 readiness
  路径，钉住 `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY` / `ONBOARD_SESSION_NOT_READY`。
- `ADoorOurOwnLoadHoldsOpenKeepsTheSessionReadyAndReachesTheAlarm`——`AwaitingLoadResult` 同一条
  路径，钉住 `Ready` 与告警码。
- `AnotherVehiclesSlotOperationDoesNotExplainThisVehiclesOpenDoor`——豁免不再跨车。

## 仍然成立的两条「不是什么」

**不是「安全投影没传过去」。**L2-DT-04 绿：服务端的 `SessionRecoveries.SafetyReasonCodesJson`
确实是 `["LOCK_NOT_CLOSED"]`。

**不是模拟器注入方式的问题。**用 `lock-feedback-override` 把锁反馈钉成 0，与真实的「机械锁没咬合」
在 `WireToGateSafetyEvaluator` 眼里是同一件事。

## 教训

与 [`20260904-recovery-required-never-reaches-session-state`](20260904-recovery-required-never-reaches-session-state.md)
仍然同族，但这次的一半是**诊断本身**踩了同一个坑：那份文档说「就绪门把整个运行时停转，门后面的分支
集体失效」，于是这次一看到就绪门就照着同一个模板下了结论，没有量第二条路。**看见熟悉的形状之后，
要量的恰恰是它与上一次不同的那一部分。**
