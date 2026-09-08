# 票 10：`FP-C11` RIoT 订单命令面与对账的验收证据

## 运行类型

纯本机 tier 1 ＋ 一次 L2 合成场景回归。**不动车、不建单、不使用任何现场凭据、不碰真 RIoT、
不需要桌面**。

## 结论

| 项 | 结果 |
| --- | --- |
| 新增测试 | 56 条（命令面 26 ＋ 急停监督器 30） |
| 全量套件 | **451 passed / 0 failed / 0 skipped**（票 08 基准 395 ＋ 56） |
| 新增 migration | **零**。`Persistence/Migrations/` 与 `ControlServerDbContext` 一行未动 |
| `Ports.cs` 改动 | **零**。新端口落在 `RiotOrderCommandPorts.cs` |
| `src/` 下 `.Raw` | 仍零命中，由票 08 的架构测试逐条证明 |
| L2 `normal-load` | PASS，行为无变化 |

## 票 08 的守卫在本票上真的挡了一次

**这是本次最值得记的一件事。**票 08 的决议预告过：「急停落地时 `TriggerEmergencyStopAsync`／
`CancelEmergencyStopAsync` 在 `DeviceClient` 上，`get_Device` 目前不在放行清单里，第一次用会红
一次。那一行照加，不是缺陷。」

实际发生的就是这个，原文见
[`ticket08-guard-catches-get-device.txt`](ticket08-guard-catches-get-device.txt)：

```
EveryRiotCallProductCodeMakesIsOnTheAllowlist [FAIL]
  Product code calls RIoT Facade methods that section 1 of the allowlist does not approve: get_Device

Failed: 1, Passed: 6
```

六个命令方法本身**一条都没红**——它们都在白名单 1.3 与 1.5 里。红的只有导航成员
`get_Device`，也就是「产品代码第一次伸手到 `DeviceClient` 这个客户端」这件事本身。确认这是
本票有意为之之后，放行清单加一行，并在测试注释里记下这一行是怎么来的。

**默认拒绝的清单按设计工作了一次，代价是一行。**

## 对账语义：命令不因为被接受就算成功

`RiotOrderCommandService.Reconcile` 是纯函数，测试逐格穷举（`fp-c11-tests.txt`）：

| 命令 | 目标态 | 落到别的终态 | 还没到 |
| --- | --- | --- | --- |
| `CANCEL` | 2 CANCELLED | Failed | Pending |
| `OrderHold` | 7 PAUSED | Failed | Pending |
| `OrderContinue` | 3 EXECUTING／5 SUCCESS | Failed | Pending |
| `HangContinue` | 3 EXECUTING／5 SUCCESS | Failed | Pending |

三条断言撑起「确认终态之前不认为命令成功」：

- **接受了但订单没动 → `Pending`，`Succeeded` 为假。**
- **调用超时但订单已经 HELD → `Confirmed`。**观察胜过调用，这正是回查而不是重试的意义。
- **读不到订单 → `Unknown`**，无论调用说了什么。

状态码取自行为实验室 `experiments/catalog.md:70`（1 QUEUEING、2 CANCELLED、3 EXECUTING、
4 FAILED、5 SUCCESS、6 DELETED、7 PAUSED、8 SUSPENDED、9 HANG、10 队列优先），Round 10／14／27
实测过其中三条。

## 急停：停车宽、恢复严

**停车宽**——三条来源全部一次通过，不需要任何额外授权；唯一的门槛是服务端操作员必须有身份，
因为「记录身份」否则无从谈起。

**恢复严**——自动解除要同时满足四件事，缺一不可，每一条缺失都有具名原因码：

| 缺的是什么 | 原因码 |
| --- | --- |
| RIoT 不是 `CAN_RECOVER` | `EMERGENCY_NOT_CAN_RECOVER` |
| 根本没有故障记录（人工／外部／来源不明） | `EMERGENCY_FAULT_FACT_ABSENT` |
| 故障 generation 已经翻篇 | `EMERGENCY_FAULT_GENERATION_MOVED` |
| 原因还没消除 | `EMERGENCY_CAUSE_NOT_CLEARED` |
| 停稳没被独立证明 | `EMERGENCY_STOP_NOT_PROVEN` |

**`CAN_NOT_RECOVER` 下一次 `cancelEmergency` 都不会发出**，有测试盯着。

退避是**从审计流算出来的到期时间，不是 sleep**：进程重启后计划仍然成立，测试里把时钟拨一拨就能
穷举。首次 2 秒、每次翻倍、封顶 30 秒，**没有次数上限**——`REQ-0248` 没有，代码里也不该有。

## 两处自审改出来的东西

### 一、解除失败原本会每个评估周期重发一次

`cancelEmergency` 被接受但闩锁没开时，episode 仍然开着，下一次评估会再发一次——**没有退避，
没有上限**。已改为与触发同一套退避。「恢复严」不该表现为对 RIoT 的猛敲。

### 二、解除未确认原本复用了「停车未确认」的告警码

两者要人跑的方向正好相反：`EMERGENCY_STOP_UNCONFIRMED` 是「车可能还在动，立刻去现场」，
解除失败是「车肯定停着，回不了岗」。用同一个码会让人白跑一趟。已拆成
`EMERGENCY_RELEASE_UNCONFIRMED`，两条都写进了现场兜底文档的告警表。

## 现场兜底

`docs/emergency-stop-field-fallback.md`，中文，给现场人员与值班工程师看。四条告警各自对应什么
动作、谁可以按物理急停（任何人，无需登录无需审批）、专业隔离归谁，以及最要紧的那一条：
**物理隔离控制现场风险，但不构成电子停稳证明，也不构成恢复资格**——车不会因为有人到过现场
而自动恢复。

## L2 回归

本票唯一碰到线上活跃路径的改动，是把 `HttpRiotMovementGateway` 的异常分类抽成
`RiotCallFailureClassification` 与新网关共用。`HttpRiotMovementGatewayTests` 直接覆盖它，L2
`normal-load` 再端到端确认一次：

见 `evidence/l2/20260908-ticket10-normal-load-001/`。
