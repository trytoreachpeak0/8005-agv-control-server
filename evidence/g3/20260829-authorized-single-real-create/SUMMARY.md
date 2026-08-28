# 授权单次真实建单：第一次真实端到端移动

## 运行类型

`AUTHORIZED_SINGLE_REAL_CREATE`。用户先给出现场物理安全 GO（关卡 210 → `N2-5_N3-5` 路线可
通行），再授权本次单次真实建单。这是本项目第一次向真实 RIoT 发出 mutation、第一次真实车辆
移动。

## 绑定输入

- ControlServer 产品：`ControlServer_MVP@9a42582654d7ca793499556522beb1547403fafb`
- package manifest SHA-256：`e40c1865e9e6753d58b3cc541dee074cfc55f38d069d9d2194e7b917e0ffdc28`
- OnboardHmi：`84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`（一次性克隆，配置未做任何覆盖）
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`
- 协议：`protocol-v0.1.1@1531489`，manifest `a467c0c4…6389f`
- MesIngest：本机 v2.4；RIoT：`http://172.19.206.222:8888`
- 隔离运行：全新 SQLite、全新 journal、临时端口 58105／58107；已安装服务未停未改，其
  `JourneyRuntime` 与建单开关全程保持关闭

有效态：`JourneyRuntime:enabled=true`、**`RiotCreateDispatch:enabled=true`**（仅本次隔离
进程的环境变量，未写入任何配置文件，进程结束即失效）。

## 执行前只读核对

| 项 | 值 |
| --- | --- |
| 位置 | 站点 210（关卡） |
| 地图 | `老厂前线new`（mapId 25） |
| 电量 | 37%（阈值 30%） |
| `sysState` / `emergencyState` | 2 / 1 |
| `multiLoadState` | 0（空载） |

## 结果：建单成功且车辆完成移动

真实订单 `order-2093389168079142912`（数值 id `1676913`），`upperId`
`W2G-94993971b3624edf81bc712d160e444a-PICKUP-1`，对应 SUBLOT `Q26081298-1|WIRE_TO_GATE`，
目标取货站 `N2-5_N3-5`（站点 21）。

RIoT 只读对账：`orderState = 5`，即 `1 QUEUEING → 3 EXECUTING → 5 SUCCESS` 的终态成功。
建单时间 `2026-08-29 01:24:06`，完成时间 `01:26:22`。

车辆 `stationNo` 由 **210 变为 21**，停在目标取货站，`sysState=2`、`locationState=3`、
电量 36%。

## at-most-once 成立

`CreateAttemptCount = 1`，`DispatchAuditVersion = 1`。审计链恰为设计顺序，无重复：

| Sequence | Phase | Outcome | ResultPresent | ReturnedOrderId |
| --- | --- | --- | --- | --- |
| 1 | `PRE_CREATE_RECONCILIATION` | `UNKNOWN` | 0 | — |
| 2 | `CREATE_DISPATCH` | `ARMED` | — | — |
| 3 | `CREATE_REQUEST` | `STARTED` | — | — |
| 4 | `CREATE_RESPONSE` | `ACCEPTED` | 1 | `order-2093389168079142912` |
| 5 | `POST_CREATE_RECONCILIATION` | `UNKNOWN` | 1 | 同上 |
| 6 | `POST_CREATE_RECONCILIATION` | `TERMINAL` | 1 | 同上 |

第 1 条即精确 absent-at-observation（HTTP 200／业务 code 0／无 result），据 BC-ORDER-004 的
upperId 幂等直接建单，未使用任何一次性 permit——这条链路的现场有效性至此得到证明。

进程收尾正常：stderr 为空，58105／58107／1502／58006 四端口全部回收，建单开关随进程结束
失效，未写入任何持久配置。

## 同时暴露的严重缺陷：成功到站被判为终态需人工对账

移动成功后 intent 变为 `Status = TERMINAL_RECONCILIATION_REQUIRED`，`JourneyRuntime` 停在
`Stage=AwaitingPickupArrival` / `BlockReasonCode=PICKUP_TerminalReconciliationRequired`，
旅程无法继续。

根因在 `HttpRiotMovementGateway.ToObservationKind`：

```csharp
1 or 3 or 7 or 9 or 10 => Active,
2 or 4 or 5 or 6 or 8  => Terminal,
```

`5 SUCCESS` 与 `2 CANCELLED` 等被一并归入 `Terminal`，而 `EnsureMovementConfirmedAsync` 只在
`MovementDispatchOutcome.Confirmed` 时放行，该结局仅由 `Active` 产生。因此旅程只能在订单
**仍在执行**时推进。

推进窗口有多窄：按 RIoT Behavior Lab 的 `state-model.md`，「物理到站早于订单 SUCCESS，约 1s
后才 `orderState=5`」，而 `IsTrustedArrivalAsync` 要求物理到站。也就是说，运行时必须恰好在
物理到站之后、`orderState=5` 之前的**约 1 秒**内轮询到，才能推进到 `AwaitingSublot`；
`JourneyRuntime:pollInterval` 为 2 秒，本次即错过该窗口。

这不是本次运行的偶然，而是取货段与关卡段共同的结构缺陷：正常成功的移动有很大概率把旅程
永久卡在到站前一步。需单独立缺陷并修复。

## 未证明的事项

本次只完成 `TO_PICKUP` 一段真实移动。装货、安全检查、`TO_GATE` 移动、关卡批量卸货与原子
完成均未进行。车辆现停留在 `N2-5_N3-5`，无活动订单。W2G-IS-00～07 的正式 G3 与 RC 保持
`INCONCLUSIVE`。
