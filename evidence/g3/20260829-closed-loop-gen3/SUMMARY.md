# WIRE_TO_GATE 端到端闭环首次走通（`3d8b00c`，generation 3）

## 运行类型

`AUTHORIZED_SINGLE_REAL_CREATE`。用户对 `210 → 21` 与 `21 → 210` 两个方向分别给出现场物理安全 GO
与逐次建单授权，操作员身份 `S0020310`。RunRoot `gen3-20260829T222602Z`，全新 SQLite 与 journal。

## 结论

**受理 → 建单 → 取货移动 → 到站认定 → 三条快照确认 → 子批录入 → 装货 → 发车安全检查 →
`TO_GATE` 移动 → 关卡到站 → 关卡批量卸货 → 四事实原子完成，首次全程走通。**
旅程终态 `Stage = Completed`，无阻断码，全程 1 分 58 秒。

这验证了此前唯一未经现场验证的修复 `3d8b00c`（每个停靠点用自己的 `worklistRevision`）。

## 绑定身份

| 组件 | 版本 |
| --- | --- |
| ControlServer | `ControlServer_MVP@3d8b00c7558ae700358f1f995a5ac75d12a3250c`（self-contained 包，`deployment-manifest.json` SHA-256 `4c7a2c34157d12897773eb5d36c4901853bf8233e11a47eab2e5f7080c17368f`）|
| OnboardHmi | `OnboardHmi_MVP@304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6`（王昆实现，配置未做任何覆盖）|
| slots-simulator | `main@fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| 协议 | `protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279` |
| MesIngest | 本机 `http://127.0.0.1:5088` |
| RIoT | `http://172.19.206.222:8888` |

车辆 `老厂前线新多仓位1` = `BROKERX-0c20ff0600d644869a6a80c186065d85`，地图 25，取货站
`N2-5_N3-5`（21），关卡站点 210，Demand `94993971-b362-4edf-81bc-712d160e444a`
（SUBLOT `Q26081298-1|WIRE_TO_GATE`）。

## 修复前后对比

同一台车、同一条 Demand。上一轮 `fullloop` 用 `f48e616` 包、generation 2；本轮 `gen3` 用
`3d8b00c` 包、generation 3。

| 指标 | `fullloop`（`f48e616`） | `gen3`（`3d8b00c`） |
| --- | --- | --- |
| `Stage` | `AwaitingUnloadResult` | **`Completed`** |
| `SessionHello` | 74 | **1** |
| `ProtocolProblem` | 74 | **0** |
| `SnapshotAppliedAck` | 4 | **6** |
| `CurrentStopWorklistSnapshot` 未确认 | 1 | **0** |
| `UpcomingStopPlanSnapshot` 未确认 | 1 | **0** |
| `SlotOperationCommand` 未确认 | 1 | **0** |
| `UnloadBatches` / `StopClosures` / `TransportDemandCompletions` | 0 / 0 / 0 | **1 / 1 / 1** |
| 车辆租约 | 未释放 | **已释放** |

`ProtocolOutbox` 本轮**全部 `unacked = 0`**。上一轮卡死的关卡工作单快照被确认，排在其后的关卡
计划快照与卸货命令随即被取到——这正是 `3d8b00c` 的失败判据被翻转的直接证据。

## 两段真实移动

均 `CreateAttemptCount = 1`、`CreateResponseAccepted` → `PostCreateReconciliationConfirmed`：

| 段 | `upperId` | 订单 | 目的站 | 状态 |
| --- | --- | --- | --- | --- |
| `TO_PICKUP` | `W2G-94993971-…-PICKUP-3` | `order-2093706784945602560` | 21 | `CONFIRMED` |
| `TO_GATE` | `W2G-94993971-…-GATE-3` | `order-2093707175561134080` | 210 | `CONFIRMED` |

`JourneyRuntimes.DispatchGeneration = 3`。第 1、2 代的订单在 RIoT 均已终结，沿用会直接对账确认
而不动车，因此本轮必须推进代次——这一点已在运行脚本注释中固化。

## 仓位操作

| 类型 | Sublot | 仓位 | 状态 | 提交时刻 |
| --- | --- | --- | --- | --- |
| `Load` | `Q26081298-1` | `[1]` | `Committed` | 22:27:41.9913117+08:00 |
| `Unload` | `Q26081298-1` | `[1]` | **`Committed`** | 22:28:03.0683663+08:00 |

两条 `OperationResult` 均 `OverallOutcome = COMPLETED`、`HistoricalOnly = 0`。物理状态由模拟器
控制面驱动，`Watch-Slots` 等仓门真正打开后才改动货物：

```
22:27:41  LOAD   slots=[1] expectedFinal=OCCUPIED  → slot 1 cargo OCCUPIED, door CLOSED
22:28:02  UNLOAD slots=[1] expectedFinal=EMPTY     → slot 1 cargo EMPTY,    door CLOSED
```

## 四事实原子完成

四者在**同一时刻** `2026-08-29 22:28:03.0683663+08:00` 提交，时间戳逐位一致：

| 事实 | 内容 |
| --- | --- |
| `UnloadBatch` | `e2056294-da2e-7759-bc0d-f7ffb25fe09a`，证据 `[{"SlotNumber":1,"State":0,"DoorLocked":true,"UnlockOutputReset":true}]` |
| `StopClosureCommit` | Demand `94993971-…-e444a` |
| `TransportDemandCompletion` | `Q26081298-1|WIRE_TO_GATE`，`DemandRevision = 1`，证据 `d6b49c46d88eaa3607f82c84b57996674e3d255f0aa7ef778383a1d25068521f` |
| `VehicleDispatchLease` | `ReleasedAt` 落于同一时刻 |

## 协议消息统计

| 出站 | 总数 | 未确认 |
| --- | --- | --- |
| `VehicleBusinessStateSnapshot` | 2 | 0 |
| `CurrentStopWorklistSnapshot` | 2 | 0 |
| `UpcomingStopPlanSnapshot` | 2 | 0 |
| `SlotOperationCommand` | 2 | 0 |
| `SublotEntryRequested` | 1 | 0 |
| `PreDepartureSafetyCheck` | 1 | 0 |

入站：`Heartbeat` 30、`SafetyStateChanged` 17、`OperationProgress` 10、`SnapshotAppliedAck` 6、
`OperationResult` 2，`SessionHello` / `SafetyStateSnapshot` / `RecoveryStateReport` /
`CapabilitySnapshot` / `SublotSubmitted` / `PreDepartureSafetyCheckResult` 各 1。
**`ProtocolProblem` 零条。**

## 运行收尾

`portsReleased = true`、`hostStderrEmpty = true`。`run-result.json` 由
`Stop-AuthorizedGateRun.ps1` 写出。

## 明确未证明的事项

**本次只证明了正常端到端旅程一条向量，不是 W2G-IS-00～07 的完整 G3。** 票据 10 要求的其余向量
——重复/乱序/延迟、不同内容冲突、断联安全收尾、进程崩溃重启、结果重放、RIoT UNKNOWN 对账与
恢复分支——均未在本轮现场覆盖。正式 W2G-IS-00～07 的 G3 与 RC 继续 `INCONCLUSIVE`；
功能性 happy path 成功不被扩大为完整切片 G3 PASS。
