# 缺陷：期限到期遇未闭仓门的告警分支在真装置上到不了——车停在恢复态，告警码从不出现

Status: open
Owner repository: `8005-agv-control-server`
Found by: [`real-onboard-station-timeout-door-open` L2 红证据](../../evidence/l2/20260909-real-onboard-station-timeout-door-open-001/SUMMARY.md)
Product at discovery: `ControlServer_MVP@0f6b42458ab55cde4c9123ad344ddc86c3fe5774`
Peers: `OnboardHmi_MVP@98df671c846c7c572fd40d38fe16e5e084686603`、`slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`

## 现象

ADR-cross-0058 决策 4 要求：站点期限到期而仓门未闭时**不结束本站**，把该站转入告警升级状态
（`BlockReasonCode = STATION_TIMEOUT_DOOR_NOT_CLOSED`）并持续等待，**stage 仍是 `AwaitingSublot`
而不是 `Blocked`**——ADR 的原话是「要的是一辆看得见地等着的车，而不是一辆停在恢复态里的车」。

真装置实跑（`AwaitingSublot`，8 号仓锁反馈钉成 0，无人扫码，站点期限压到一分钟）：

| 观测 | 期望 | 实际 |
| --- | --- | --- |
| 车辆上报的安全原因码 | 含 `LOCK_NOT_CLOSED` | `["LOCK_NOT_CLOSED"]` ✅ |
| `SessionRecoveries.Readiness` / `ReasonCode` | `Ready` / `READY` | **`RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY`** ❌ |
| `JourneyRuntimes.BlockReasonCode` | `STATION_TIMEOUT_DOOR_NOT_CLOSED` | **`ONBOARD_SESSION_NOT_READY`** ❌ |
| `JourneyRuntimes.Stage` | `AwaitingSublot` | `AwaitingSublot` ✅ |
| `AcceptedDemands.Status`（门开着时） | `Accepted` | `Accepted` ✅ |
| 门闭合之后 | `Completed` / `CANCELLED_BY_STATION_TIMEOUT` / `Cancelled` | 同左 ✅ |

**ADR 要的结果成立了，机制不是它写的那条。**本站没有被关闭、需求没有被取消、门一闭合就按当时的
读数结算——这三件事都对。但它们是**就绪门**的副作用，不是决策 4 那段代码干的：那段代码一次都没有
被执行到。

## 根因

`JourneyRuntimeEngine.AdvanceAsync` 的第一件事就是就绪门：

```csharp
SessionRecoveryRow? session = await CurrentReadySessionAsync(runtime.AgvId, cancellationToken);
if (session is null)
{
    // ... runtime.BlockReasonCode = "ONBOARD_SESSION_NOT_READY";
    return;
}
```

决策 4 的 `TryTimeOutSublotWaitAsync` 在这道门**之后**，位于 `case JourneyRuntimeStage.AwaitingSublot`
分支里。所以它要跑起来，会话必须在报着 `LOCK_NOT_CLOSED` 的同时仍然 `Ready`。

而让会话在这种情况下保持 `Ready` 的唯一途径是 `WireToGateStore.DecideReadinessAsync` 里的

```csharp
bool departureUsable = row.DepartureSafe == true ||
                       await IsUnsafetyExplainedByOwnCommandAsync(row, cancellationToken);
```

`IsUnsafetyExplainedByOwnCommandAsync` 的最后一个条件是

```csharp
return await dbContext.StationOperations
    .AnyAsync(item => item.Status == StationOperationStatus.Prepared, cancellationToken);
```

**它要求存在一个正在执行的仓位命令。**而 `AwaitingSublot` 这个阶段的定义就是一个命令都还没发出去
（`TryTimeOutSublotWaitAsync` 自己的注释写着 "no slot operation was ever commanded"）。两个条件互斥，
所以在真实系统里「`AwaitingSublot` + 仓门未闭 + 会话 Ready」这个状态不存在。

## L1 为什么是绿的

`JourneyRuntimeWorkerTests.AnExpiredStationDeadlineDoesNotCloseTheStopWhileASlotDoorIsStillOpen`
用的是夹具方法 `ReportDoorLeftOpenAsync`，它**直接写 `SessionRecoveryRow` 的安全字段而不重算
readiness**：

```csharp
session.SafetyReasonCodesJson = "[\"LOCK_NOT_CLOSED\"]";
session.SafetyUnknownPresent = false;
```

`Readiness` 保持建夹具时的 `Ready`。**分支本身是对的，到不了的是那个状态。**这与
[`20260904-recovery-required-never-reaches-session-state`](20260904-recovery-required-never-reaches-session-state.md)
是同一族问题：就绪门把整个运行时停转，于是门后面那些为特定情形写的分支集体失效。那份文档最后一节
说「教训比缺陷本身更值得记」，这就是它的第二次兑现。

## 影响

不是「车会带着开着的门跑掉」——那条底线守住了，而且守得比 ADR 设想的更早。真正的代价有两条：

1. **告警码不存在。**ADR 决策 4 把「告警升级的对象、通道与节奏」交给现场规程，前提是服务端能表达
   出这个状态。现在服务端表达出来的是 `ONBOARD_SESSION_NOT_READY`，那是一个说「车载端会话不可用」
   的通用码，它既不说明是哪扇门、也不区分「有人把门虚掩着」与「车载端掉线了」。现场规程没有东西
   可以挂靠。
2. **车停在恢复态，正是 ADR 明说不要的那一种。**`SessionRecoveries.Readiness = RecoveryRequired`
   会让车载端 HMI 的恢复入口具备显示条件（如果 `wireToGate.recoveryResumeEnabled` 打开的话），
   把「把仓门带上」这件事重新包装成一次需要 `MAINTENANCE_ADMINISTRATOR` 的恢复握手——
   ADR-cross-0058 通篇要消除的就是这个。

## 不是什么

**不是「安全投影没传过去」。**L2-DT-04 绿：服务端的 `SessionRecoveries.SafetyReasonCodesJson`
确实是 `["LOCK_NOT_CLOSED"]`，`DoorLeftOpen` 读的那个字段值是对的。判据来源没有问题，
问题是读它的那段代码到不了。

**不是模拟器注入方式的问题。**用 `lock-feedback-override` 把锁反馈钉成 0，与真实的「机械锁没咬合」
在 `WireToGateSafetyEvaluator` 眼里是同一件事（`snapshot.Lockers.All(locker => locker.IsLocked)`
为假 → `LOCK_NOT_CLOSED`）。换成真实的虚掩门，走的是同一条判定。

## 可能的修法（未定）

三条都动了别处的语义，所以本文档只记问题，修法留给 ADR 层面决定：

1. **把决策 4 的判定挪到就绪门之前**，与 `TryBlockOnRecordedRecoveryAsync` 并列——那个函数正是为了
   「不让就绪门关在最需要说明阻塞原因的旅程上」而放在门前的，形状完全一样。
2. **把 `IsUnsafetyExplainedByOwnCommandAsync` 的豁免条件放宽**到「本站停靠期间」而不是「有
   `Prepared` 命令」。风险大：那条豁免的注释明确说「刻意收窄」，放宽会让别的场景下的仓门未闭也
   不再降级会话。
3. **在 `ONBOARD_SESSION_NOT_READY` 之外增设一个更具体的原因码**，让现场规程有东西可挂。这只解决
   影响 1，不解决影响 2。

本缺陷与 [`8005-agv-program#24`](https://github.com/trytoreachpeak0/8005-agv-program/issues/24)
相邻但不同：那一票问的是「服务端结算本站之后怎么中止在途的仓位操作」，本条问的是「本站根本没能
走到结算那一步」。两者都指向决策 4 与决策 5 之间那段没有接上的路。
