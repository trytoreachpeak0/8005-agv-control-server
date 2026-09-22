# 缺陷：派往取货站的那一版计划在车载端看到本服务端的 RIoT 单之后就再也发不出去

Found by: 批次 7 出口 G3（control-server#220 第 4 步），[`evidence/g3/20260922-protocol-v2.0.0-journey-517e1c7a/scenarios/g3-task-type-admission-fail-closed/SUMMARY.md`](../../evidence/g3/20260922-protocol-v2.0.0-journey-517e1c7a/scenarios/g3-task-type-admission-fail-closed/SUMMARY.md)
Status: 已登记，修复去向待调度定（出口票不改产品代码）
Owner repository: `8005-agv-control-server`（`JourneyRuntimeEngine` 的就绪闸门与 `PublishPickupDispatchPlanOnceAsync`）
Product at discovery: control-server `517e1c7a`，onboard-hmi `ecdb3a0b`，slots-simulator `fb5f7c59`，protocol `protocol-v2.0.0`（`86575456`）

## 现象

journey G3 15 个场景里 14 个 PASS，`g3-task-type-admission-fail-closed` 在判据之前中断：

```
Timed out after 60s waiting for: the onboard acknowledged the plan sent before the first arrival. Last observed: 0
```

它的判据因此一条都没写出来，journey 整轮是 `JOURNEY_G3_SLICE_FAIL`。同一轮里走同一个等待的另两个场景
（`g3-journey-demand-to-pickup`、`g3-reversed-direction-journey`）都 PASS，那份计划 0.3 秒内就被确认。

## 因果链（失败现场的服务端数据库，`stage-db-facts.txt`）

| 时刻（UTC） | 事实 | 出处 |
| --- | --- | --- |
| 05:34:06.14 | 车载端会话建立，`generation=1`、`Ready` | 车载端日志 |
| 05:34:12.22 | 需求受理，旅程行建立 | `JourneyRuntimes.CreatedAt` |
| 05:34:12.79 | 取货那一段的 RIoT 单建成并确认（`PostCreateReconciliationConfirmed`），车辆占用写入 | `OrderIntents` |
| 05:34:12.87 | 车载端报 `OnboardAlarmSnapshot`（`ONBOARD_DEPARTURE_SAFETY_SIGNAL_UNAVAILABLE`）与 `SafetyStateChanged` | `ProtocolInbox` |
| 05:34:12.89 | 会话记录变成 `RecoveryRequired` / `DEPARTURE_SAFETY_NOT_READY`，安全原因 `["VEHICLE_NOT_READY"]`、`unknownPresent=1` | `SessionRecoveries` |
| 05:34:12.99 | 运行时的就绪闸门把旅程标成 `ONBOARD_SESSION_NOT_READY`，本轮不发任何东西 | `JourneyRuntimes.BlockReasonSince` |
| 05:34:12～05:35:12 | 心跳每 2 秒一条照常到达（33 条），会话记录再没更新 | `ProtocolInbox` |
| 收尾 | `ProtocolOutbox` **0 行**：派往取货站的计划从未写进发件箱 | `ProtocolOutbox` |

车载端的 `VEHICLE_NOT_READY` 在这里不是故障：服务端的车辆安全接口在车上挂着**本服务端自己建的**未结束 RIoT 单时加
`RIOT_NONFINAL_ORDER_PRESENT` 并回 `motionState=Unknown`（control-server#138，`HttpRiotMovementGateway`），车载端据此报
「是否停稳未知」。这是 RC 第 8 节的闸门，按设计保留（`OwnMovementOrderExplanation` 头注释）。

## 所以

派往取货站的那一版计划只在 `AdvanceAsync` 里、就绪闸门之后发（`PublishPickupDispatchPlanOnceAsync`）。而 RIoT 单是在
受理那一刻（`WireToGateOrchestration`）建成并确认的。**从单被确认到运行时下一轮推进之间有一个窗口**：车载端只要在这个窗口里
读到车辆安全接口，就进 `RecoveryRequired`，而车上挂着那张单时它不会回到 `Ready`，所以这一版计划此后再也发不出去。
这次窗口约 200 毫秒（12.79 → 12.99），车载端在 90 毫秒时翻了。

在 G3 里这是死锁：装置要等计划确认之后才让假 RIoT 把车开走，而车不开走，那张单就不会结束。**在现场不会死锁**，车照 RIoT
单开走、到站后单结束、会话回 `Ready`，到站那一版计划照发。现场的后果是：**车在路上时车载端没有这趟的计划**（看不到要去哪、
下一站是什么），直到到站。

## 不是第一次

control-server#211 的 G3 自检（`evidence/g3/b7-06-journey-final-head/README.md`）在 `g3-reversed-direction-journey` 上撞过**完全
相同的形状**：`VEHICLE_NOT_READY` → `ONBOARD_SESSION_NOT_READY` → 计划不再发出 → 60 秒不恢复。当时单独重跑绿，记为「没有查清为什么
车载端 60 秒不报恢复」，调度 09-21 接受为环境偶发。**第二次撞上、位置不同（换了一个场景），说明它不是某个场景的问题，是这个窗口本身**。
当时那一条「没有查清」的答案就是上面那一段：车上挂着本服务端的单，车载端按设计不会恢复。

## 与 cs#307 形状的区别

cs#307 是「握手完成后服务端停止应答、车载端 `TimeoutException` 自断」。这里连接一直活着（心跳每 2 秒照常到、服务端照常受理），
车载端没有断线；服务端是**按闸门主动不发**。不是同一个形状。

## 窗口什么时候就有（读到的与推出的分开）

**读到的：**

- 结构在批次 6 就是这样：`e74c0058` 的 `JourneyRuntimeEngine` 一轮里先推进已有旅程、再受理新需求（注释原文「a journey created this
  round is not advanced until the next one」），受理时 `AcceptAndDispatchToPickupAsync` 就建单并确认。现在的代码同样如此。
- 车载端车辆安全信号的轮询周期默认 1 秒（`ControlServerVehicleSafetySignalProvider`，`PollIntervalMs`），`44b3aa6e..ecdb3a0b` 之间
  这个文件、`WireToGateSafetyEvaluator`、`OnboardAlarmEvaluator` 都没改过。
- 库里全部 journey G3 证据，按「走到『等派往取货站的计划被确认』这一步」计的机会与命中（`evidence/g3/*/…/timeline.jsonl`）：
  批次 5、6 的六轮（`052759bc`、`06b65688`、`c12f0498`、`d3003c2f`、`905ffd1d`、`85381ea2`）共 10 次机会、0 次命中，
  从派出到确认 0.31～0.65 秒；批次 7 入库的是 cs#211 自检那轮（`fc144d0a`，入库只留了红的那个场景）、它的单跑重跑、本票这一轮，
  共 5 次机会、2 次命中。

**推出的（没有量过）：**

- 被撞上的概率约等于「单确认 → 下一轮推进」的窗口长度除以车载端轮询周期。这次窗口约 200 毫秒，对 1 秒的轮询约两成。
- 0/10 对 2/5 提示批次 7 把窗口拉宽了（例如每轮推进之前的工作变多），但机会数太少，不能据此下结论；也没有找到是哪一次合入。
- 现场真 RIoT 与车载端的节奏下被撞上的概率没有量过。

## 修复去向

出口票不改产品代码（票面「冲突边界」）。修法候选（供开票时取舍，不在这里定）：派往取货站的计划随受理那一刻一起写进发件箱
（在建单之前或同一事务里），或让就绪闸门对「只因本服务端自己的在途单而未就绪」放行这一版计划的下发。
