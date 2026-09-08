# 缺陷：服务重启后会话落入 RecoveryRequired，车静止时永远不会自己出来

Status: fixed（修复已交付，待合并与现场复跑）
Fixed in: `8005-agv-onboard-hmi` commit `004891f`，PR
[#18](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/pull/18)（叠在 #17 上，因为两者改同一
个文件）。**现场复跑仍待安排**——发现条件是真实服务重启加一台真车，两者 L2 都不提供。
Owner repository: **`8005-agv-onboard-hmi`** —— 现象在服务端的 `readiness` 上，根因在车载端的安全
快照去重里，见下面的根因一节。本文档留在本仓，因为它是从服务端的观测查起的；真正动手要在
`8005-agv-onboard-hmi` 的 `w2g/*` 分支并以 PR 交付。
Found by: 真车实跑（`agv01`，2026-09-08 上午到中午连续五趟 WIRE_TO_GATE journey，真实
Modbus IO 模块 `192.168.71.150:502`）。**这不是 L2 证据**——它需要一次真实的服务重启加一台真车，
两者 L2 都不提供。
Product at discovery: `ControlServer_MVP`，部署于 `172.19.205.222`，protocol `0.1.1` /
`WIRE_TO_GATE_MVP`
Peers: 车载端 `OnboardHmi_MVP`，`onboardBuildCommit=1d584aff22293056c06580ab3a0a957245ae9648`

---

## 现象

ControlServer 服务重启后，车载端在两秒内重连，服务端在握手当场把会话判为
`RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY`。此时链路是好的、车是停的、
`/api/onboard/v1/vehicle-safety` 主动查询返回 `motionState=STOPPED` 且证据只有几百毫秒新，
但会话就是不回 `Ready`。

车载端日志（`C:\8005\OnboardHmi\logs\agv-20260908.log`）：

```
12:40:38.72  2号仓IO变化：DO 0->0，锁DI 0->1，光幕DI 1->1。      卸货最后一仓关闭
12:40:40.35  忽略重复SlotOperationCommand：attempt=67a90e2c-...，保留原OperationResult重放。
12:40:43.00  上层会话不可用：WIRE_TO_GATE会话尚未建立。。将在2秒后重连。
12:40:46.65  上层会话已建立：generation=94，readiness=RecoveryRequired。
```

`12:40:46` 之后车载端日志**再无任何一行**，直到 `12:47:22` 被人为重启。同一时段服务端
`/api/runtime/sessions` 始终返回：

```json
[{"agvId":"老厂前线新多仓位1","sessionGeneration":94,"readiness":"RecoveryRequired",
  "reasonCode":"DEPARTURE_SAFETY_NOT_READY","updatedAt":"2026-09-08T04:40:46.6234679+00:00"}]
```

`/health/ready` 同时以 `HTTP 503 RECOVERY_HANDSHAKE_REQUIRED` 拒绝。**这个状态持续了 6 分 36 秒，
直到重启车载客户端才结束**；重启后建立的 generation 95 立刻就是 `Ready` / `READY`。

## 决定性的对照：同一天的三次

| # | 时刻 | 掉入 RecoveryRequired 时车在做什么 | 结果 |
| --- | --- | --- | --- |
| 1 | 12:40:46 | journey 已 `Completed`，**车停着，无事发生** | **6 分 36 秒未自愈**，人为重启客户端才恢复 |
| 2 | 12:53:49 | journey 飞行中（`AwaitingGateArrival`），**车装着货在去关卡路上** | **2 分 7 秒后自愈** |
| 3 | 12:55:58 | 守护收尾，journey 已 `Completed` | 未掉，会话保持 generation 97 `Ready` |

第 2 次的自愈过程留下了机制证据。服务端 `ProtocolInbox` 最后一条：

```
RecoveryStateReport @ 2026-09-08 04:55:56.5143415+00:00
sessions: generation 97, readiness Ready, READY, updatedAt 04:55:56.5644505+00:00
```

**车载端自己发了一份 `RecoveryStateReport`，服务端收到后才把会话放回 `Ready`**，相隔 50 毫秒。
那两分钟里 journey 一直卡在 `AwaitingGateArrival` / `ONBOARD_SESSION_NOT_READY`，车带着货在跑。

## 判断

`OnboardMessageProcessor.cs` 里那段 OPEN 注释准确预言了这件事：

> OPEN: after a recovery completes, nothing on this path tells the vehicle the session is READY
> again. Today the onboard learns it from its own next `RecoveryStateReport`. **If that turns out
> not to happen**, widen this rather than adding a second announcement somewhere else.

三次对照说明「that」在什么条件下不会发生：**车载端只在自身状态发生变化时才发
`RecoveryStateReport`。** 车在移动、要到站、要停车，这些都是变化，报告自然会来（第 2 次）；
而 journey 结束后车静止在原地，没有任何事件推动它，那份报告就永远不会发出（第 1 次）。

于是恢复条件与需要恢复的时刻正好错开：**最需要它的静止态，恰恰是它永远不来的那一种。**

这在产线上比在测试台更严重。测试台有人看着，可以重启客户端；产线上 journey 结束后车停在那里
等下一单，没有人会去重启一个「看起来正常」的客户端——HMI 上没有报错，车也没有异常，只是
再也接不到活。

## 根因（2026-09-08 当天查到，位置在车载端）

`DecideReadinessAsync` 的 `ready` 是七个条件的合取，而 `GetRecoveryReason` **按顺序**返回第一个
不成立的那个。线上报的是列表中第六位的 `DEPARTURE_SAFETY_NOT_READY`，**这本身就证明前五项全部
成立**——`CapabilityRevision`、`SafetyRevision`、`RecoveryReportId` 都不是 null，强制恢复代际匹配，
没有待对账事实。握手是完整的，唯一不成立的是：

```csharp
bool departureUsable = row.DepartureSafe == true ||
                       await IsUnsafetyExplainedByOwnCommandAsync(row, cancellationToken);
```

右半边要求存在 `StationOperationStatus.Prepared` 的操作，旅程跑完时不会有，恒为假。所以
`departureUsable` 完全取决于 `row.DepartureSafe`，而它只有一个来源：车载端发来的
`SafetyStateChanged` 里的 `safety.departureSafe`。

**服务端这一侧没有可修之处。**把 readiness 重算挂到心跳上是无效的——`DepartureSafe` 是持久化的
`false`，重算多少次都是同一个答案。必须让车载端重新上报。

车载端不重报的原因在 `8005-agv-onboard-hmi` 的
`src/SQCD.Agv.Wpf/WireToGateBusinessService.cs`：

```csharp
private string? _lastSafetySignature;                                    // 第 53 行
...
if (_pendingSafetyChange is null
    && string.Equals(_lastSafetySignature, signature, StringComparison.Ordinal))
{
    return;                                                              // 第 707-711 行：签名没变就不发
}
```

`_lastSafetySignature` 只出现四次：声明（53）、比较（708）、两处赋值（698、726）。
**没有任何地方在会话换代时重置它。**它是进程内的去重状态，而会话不是——服务端重启后新会话从零
开始、手上没有上一代的安全快照，车载端这边进程没重启、签名照旧，于是那份快照永远不会重发。

这也解释了三次观测的差异：车在移动时安全签名本来就不同，停车后自然变化一次并触发重发（第 2 次
自愈）；而车静止不动时签名恒定，永远不会跨过那道 `return`（第 1 次卡死）。

**修复方向**：会话代际变化时把 `_lastSafetySignature` 置空，使下一次评估必定重发一份全量快照。
这正是 ADR-cross-0022「连接时全量同步，变化时可靠增量」要求的语义——现行实现做到了后半句，
漏了前半句在**重连**时同样适用。`WireToGateSessionSnapshot.SessionGeneration` 是 `long?`，
在 `QueueSafetyStateChangeAsync` 取到 `_session.Current` 之后（第 694 行）比对并重置即可。

**为什么本次没有直接改**：`8005-agv-onboard-hmi` 属于 Kun Wang，只能在 `w2g/*` 分支改并以 PR
交付；而 2026-09-08 当天该仓工作树有十个文件的未提交改动（`w2g/multi-demand-worklist` 上的多单
改造），其中就包括 `WireToGateBusinessService.cs`。在同一个文件上叠加会把两件事混成一团，因此
只留方案不动代码。

## 修复落地时查到的两件事（2026-09-08 晚）

**本文档上面那句「`DepartureSafe` 只有一个来源：`SafetyStateChanged`」是错的。**
`OnboardMessageProcessor.cs:196` 处理 `SafetyStateSnapshot` 时同样调
`store.ApplySafetySnapshotAsync(..., departureSafe, ...)`，而 `SafetyStateSnapshot` 是每次干净
重连都会发的。所以服务端在换代握手里本来就会拿到一份 `departureSafe`。这不改变修复方向——
ADR-cross-0022 要求的「连接时全量同步」在车载端这一侧确实没做到，签名去重跨代不重置是实打实的
缺陷——但它说明**本文档对现场那 6 分 36 秒的机制解释还没有闭合**：握手里那份快照当时携带的
`departureSafe` 是什么值，没有证据。现场复跑时应当把 `SafetyStateSnapshot` 的 payload 一并抓下来。

**`FakeControlServer` 建模不了干净重连，所以现场最常见的那条路径在车载端没有测试覆盖。**
它跨重连保留快照 revision 记忆（`SameRevisionDifferentContentFailsClosedWithProtocolProblemReasonCode`
依赖这一点），而重连必然换 `sessionGeneration`、整信封哈希必然变，于是干净重连一定以
`SNAPSHOT_REVISION_CONTENT_CONFLICT` 收场。真实服务端不是这样：
`WireToGateStore.BeginSessionRecoveryAsync` 在每个新代际把 `CapabilityRevision`、`SafetyRevision`、
两个哈希与 `DepartureSafe` 全部置空——现场那次实跑里 generation 94→95→96→97 都建立成功，也证明
重连本身是能成的。分歧在假服务端一侧，值得单独收拾。

## 尚未查清的一点

原先这里列了两点，**第一点已经查清，而且当时的猜测是错的**，留下经过：本文档第一版猜
「服务刚启动几秒、手上还没有车辆安全证据，于是保守判定」。实际不是——握手是完整的，服务端拿到了
安全快照（`SafetyRevision` 非 null 才会走到 `DEPARTURE_SAFETY_NOT_READY` 这一档），只是那份快照
里的 `departureSafe` 是 `false`，而车载端因为进程内签名未变再也不重发。**问题不在服务端的判定，
在车载端的去重。**见上面的根因一节。

仍未查清的是：**第 3 次为什么没掉。** 同样是守护收尾、同样是 journey 已 `Completed`，会话却保持
了 `Ready`。`updatedAt` 停在 12:55:56 未变，说明会话根本没有重建，可能那一次服务并未真正重启。
没有查证，但它说明这不是每次必现，而是**时序竞态**——第 2 次自愈前那两分钟，车带着货被 block 在
`AwaitingGateArrival`，同一个竞态在飞行途中同样会咬人。

## 复现

需要真车与真实服务重启，L2 无法覆盖：

1. 跑完一趟 WIRE_TO_GATE journey 到 `Completed`，让车停在关卡。
2. 重启 `8005 AGV ControlServer` 服务（`13-close-gates-when-idle.ps1` 的收尾本身就会做这件事：
   它必须重启服务才能让关闭的闸生效，因为 `JourneyRuntimeWorker` 只在启动时读一次 `IOptions`）。
3. 观察 `/api/runtime/sessions`。落入 `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY` 后保持不动。

注意第 2 步：**这条路径不是人为构造的**，它是收闸机制的正常组成部分，每趟结束都会走一次。

## 影响面

- 车在 journey 之间静止时最容易中招，而那正是产线的常态。
- 车载端 HMI 不显示异常，`vehicle-safety` 主动查询一切正常，只有服务端的 `readiness` 是坏的，
  现场难以察觉。
- 唯一已知出路是重启车载客户端（`10-start-onboard-stack.ps1 -Stop` 后再启动），属于运维动作，
  不是产品功能。
- 与 ADR-cross-0058 关系：那条讲的是操作员不作为时的收敛，本条是会话就绪本身卡死，两者独立；
  但本条会让 0058 第 3、4 条依赖的服务端期限根本跑不起来——引擎停转时没有任何期限在走。
