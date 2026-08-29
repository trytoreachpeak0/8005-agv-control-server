# 装货到关卡到站首次走通：五处服务端修复的现场验证

## 运行类型

`AUTHORIZED_SINGLE_REAL_CREATE`。用户对 `210 → 21` 与 `21 → 210` 两个方向分别给出现场物理安全
GO 与逐次建单授权。本节归档 2026-08-29 晚间六次绑定运行，验证
[到站之后首次走通](../20260829-arrival-to-sublot-field-verify/SUMMARY.md) 之后的五处服务端修复。

## 结论

**装货、发车安全检查、`TO_GATE` 移动、关卡到站四段首次走通。** 旅程从 `AwaitingSublot` 前进到
`AwaitingUnloadResult`，两段真实移动均以 `CreateAttemptCount = 1` 完成。

**关卡批量卸货与四事实原子完成仍未走通**，精确原因见下节，修复为 `3d8b00c`，该修复尚未现场复跑。

## 逐次运行的推进图谱

六次运行同一台车 `BROKERX-0c20ff0600d644869a6a80c186065d85`、同一条 Demand
`94993971-b362-4edf-81bc-712d160e444a`（SUBLOT `Q26081298-1|WIRE_TO_GATE`），每次全新 SQLite 与
journal。每行的服务端提交为该次运行所用的包。

| 运行 | 服务端 | 到达 `Stage` | 阻断 | `SessionHello` | `ProtocolProblem` |
| --- | --- | --- | --- | --- | --- |
| `loadfix` | `060dba9` | `AwaitingLoadResult` | `ONBOARD_SESSION_NOT_READY` | 16 | 0 |
| `resulthash` | `ea8dc98` | `AwaitingDepartureSafety` | `PRE_DEPARTURE_SAFETY_NOT_VALID` | **1** | 0 |
| `safetywait` | `12eddf2` | `AwaitingDepartureSafety` | `PRE_DEPARTURE_SAFETY_NOT_VALID` | 1 | 0 |
| `correlation` | `31569f5` | `AwaitingGateArrival` | `GATE_CreateDispatchDisabled` | 1 | 0 |
| `gate` | `f48e616` | `AwaitingUnloadResult` | 无 | 35 | 35 |
| `fullloop` | `f48e616` | `AwaitingUnloadResult` | `ONBOARD_SESSION_NOT_READY` | 74 | 74 |

六次运行的 `host.err.log` 均为 0 字节。

## 五处修复与其现场效果

### 1. 结果哈希按对端口径重算（`ea8dc98`）

`loadfix` 中装货操作停在 `Prepared`、16 次会话重建且无 `ProtocolProblem`——对端不是按协议拒绝，
而是服务端判内容冲突后直接拆连接，对端每两秒重放一次结果。第一因：对端在业务内容进入 wire 之前
按 CLR 值计算 `OperationResult` 哈希，其 `observedAt` 由 `DateTimeOffset` 转换器原样写出时区的
`+`；服务端复制收到的 `JsonElement` 重建内容，经 encoder 把 `+` 转义成六字符 unicode 转义，两端
哈希不可能相等。

现场效果：`resulthash` 中装货操作首次 `Committed`，`SessionHello` 由 16 降为 **1**。

### 2. 发车安全证据在有效期内判读（`12eddf2`）

对端在数十毫秒内答复发车安全检查，并附一个短于一个轮询周期的有效期；引擎下一轮才回来取答复，
窗口已关闭。改为发出后短暂等待并立即判读，不放宽任何条件。

### 3. 接受对端实际使用的关联方式（`31569f5`）

`safetywait` 证明第 2 项不足以解除阻断：旅程仍停在 `AwaitingDepartureSafety`。真因是对端按 check
id 关联 `PreDepartureSafetyCheckResult`，而服务端要求承载它的请求 messageId。协议要求该消息带
`correlationId`，却从未规定它关联什么，其自带的合法样例两者都不指向。

**这一格是当日方法论教训的现场记录**：`12eddf2` 提交时把失败判据命名为「十二个条件里只有时效
不满足」，但当时只核了 `safetyStateVersion`，未把十二条逐条分离。`safetywait` 运行推翻了该判断。
`12eddf2` 本身仍然必要（对端的有效期窗口确实短于一个轮询周期），但它不是拒绝该答复的原因。

现场效果：`correlation` 中发车安全检查首次通过，旅程进入 `AwaitingGateArrival`，因建单开关关闭
而正确停在 `GATE_CreateDispatchDisabled`——该次运行未建第二单，`TO_GATE` 保持
`PENDING_RECONCILIATION`、`CreateAttemptCount = 0`。

### 4. 业务结果答复的命令不再被无限重放（`f48e616`）

`SublotEntryRequested`、`SlotOperationCommand`、`PreDepartureSafetyCheck` 的答复分别是
`SublotSubmitted`、`OperationResult`、`PreDepartureSafetyCheckResult`，都不是 `DurableAck`，因此
它们的 outbox 行从未被标记已确认，会重绑到新 session generation 后重放进每一个后续会话，被对端
判为同一业务 id 内容改变并断连。

现场效果：`gate` 中 `TO_GATE` 首次真实建单并到站，旅程首次进入 `AwaitingUnloadResult`。
`fullloop` 的 outbox 中 `SublotEntryRequested` 与 `PreDepartureSafetyCheck` 均 `unacked = 0`。

### 5. 每个停靠点用自己的 `worklistRevision`（`3d8b00c`，**尚未现场验证**）

`gate` 与 `fullloop` 到达关卡后分别出现 35 次与 74 次 `ProtocolProblem`，原因码全部相同。
`fullloop` 最后一条（`sessionGeneration = 74`）：

```
rejectedMessageType: CurrentStopWorklistSnapshot
reasonCode:          SNAPSHOT_REVISION_CONTENT_CONFLICT
```

取货与关卡的工作单站点不同、角色不同、停靠点不同，却都以 revision 1 发出——`fullloop` 结束时
`JourneyRuntimes.WorklistRevision` 仍为 `1`。对端按类型与 revision 判定快照身份，正确地把第二条
读作「内容未变」并拒绝。

`fullloop` 的 outbox 直接坐实其后果：

| MessageType | 总数 | 未确认 |
| --- | --- | --- |
| `CurrentStopWorklistSnapshot` | 2 | **1** |
| `UpcomingStopPlanSnapshot` | 2 | **1** |
| `SlotOperationCommand` | 2 | **1** |

第二条工作单快照从未被确认，排在其后的关卡计划与**卸货命令**因此从未被取到，关卡段无法开始。

## `fullloop` 的终态事实

两段真实移动，均 `CreateAttemptCount = 1`、`CreateResponseAccepted` →
`PostCreateReconciliationConfirmed`：

| 段 | `upperId` | 订单 | 目的站 | 状态 |
| --- | --- | --- | --- | --- |
| `TO_PICKUP` | `W2G-94993971-…-PICKUP-2` | `order-2093689119296323584` | 21 | `CONFIRMED` |
| `TO_GATE` | `W2G-94993971-…-GATE-2` | `order-2093690819126099968` | 210 | `CONFIRMED` |

仓位操作：

| 类型 | Sublot | 仓位 | 状态 |
| --- | --- | --- | --- |
| `Load` | `Q26081298-1` | `[1]` | **`Committed`** |
| `Unload` | `Q26081298-1` | `[1]` | `Prepared`（命令已建，结果未回） |

装货结果 `OverallOutcome = COMPLETED`、`HistoricalOnly = 0`。入站消息中
`SublotSubmitted`、`PreDepartureSafetyCheckResult`、`OperationResult` 各 1 条，
`JourneyRuntimes` 的 `ConsumedSublotMessageId` 与 `ConsumedSafetyResultMessageId` 均已落值。

**四事实原子完成未发生，且按设计 fail-closed**：`UnloadBatches`、`StopClosures`、
`TransportDemandCompletions` 三张表均为 **0 行**，`VehicleDispatchLeases` 的 `ReleasedAt` 为空
——车辆租约仍占用原 Demand，未误报完成、未释放。

## 未走通与下一步

| 段 | 状态 | 前置 |
| --- | --- | --- |
| 关卡批量卸货 | 未走通 | `3d8b00c` 现场复跑 |
| 四事实原子完成 | 未到达 | 上一段 |

`3d8b00c` 已有单元测试与绑定自身的八片 G2（`artifacts/g2/issue10-3d8b00c/`，137 个筛选测试、
0 skip，集合 SHA-256 `a3c0401caa404473b24d243cd5fd3db99604c23d70a14a9064ced1b9054c003d`），但
**没有跑过现场**。验证需要一轮完整闭环，会再动两次车，并且必须把
`JourneyRuntime__dispatchGeneration` 推进到 `3`——第 1、2 代的 `PICKUP`/`GATE` 订单在 RIoT 均已
终结，沿用会直接对账确认而不动车。

正式 W2G-IS-00～07 的 G3 与 RC 仍为 `INCONCLUSIVE`。本文不把上述四段表述为切片 G3 PASS。
