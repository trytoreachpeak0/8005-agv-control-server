# 缺陷：服务重启后会话落入 RecoveryRequired，车静止时永远不会自己出来

Status: open
Owner repository: `8005-agv-control-server`
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

## 尚未查清的两点

写下来是为了不让后来者以为这份文档已经定案：

1. **服务端为什么在握手当场判 `DEPARTURE_SAFETY_NOT_READY`。** 合理的猜测是服务刚启动几秒、
   手上还没有车辆安全证据，于是保守判定——但没有读过那段代码，不作断言。若确实如此，问题就分成
   两半：握手时的保守判定是对的，缺的是**之后的重新评估**。
2. **第 3 次为什么没掉。** 同样是守护收尾，同样是 journey 已 `Completed`，会话却保持了
   `Ready`。`updatedAt` 停在 12:55:56 未变，说明会话根本没有重建，可能那一次服务并未真正重启。
   没有查证，但它说明这不是每次必现，而是**时序竞态**。

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
