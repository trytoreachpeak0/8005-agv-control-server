# 零 mutation 双端演练：建单开关关闭下的真机拒绝分支

## 运行类型

`ZERO_MUTATION_PEER_REHEARSAL_NOT_G3`。本次不是任何切片的 G3。目的有二：在真实 MesIngest、
真实 RIoT、真实 OnboardHmi 与真实 slots-simulator 下取得 `CreateDispatchDisabled` 拒绝分支的
运行态证据，并确认关闭态演练不消耗 intent 的建单资格。

## 绑定输入

- ControlServer 产品：`ControlServer_MVP@1a0158c87c36fb2e3e0f4ddca1f7ed9c84d5672b`
- package manifest SHA-256：`3475c33e1962eafb0e076afb94bea496af8730a0551dd990c1fe822005f84c4b`
- OnboardHmi：`84b7f3f66ff2f867b18121760f38e26e0bbd6fa5`（一次性克隆构建，原仓库未改动）
- slots-simulator：`fb5f7c593742bf98bc3957b8729a38aad5321f28`（同上）
- 协议：`protocol-v0.1.1@1531489e42e328f28bfe0c51ed3f8c56e5ce0279`，manifest
  `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`
- MesIngest：本机 `http://127.0.0.1:5088`，contract `2026.08.new-mes-ingest.v2.4`
- RIoT：`http://172.19.206.222:8888`

隔离运行使用全新 SQLite、全新 Onboard journal 与临时端口 58105／58107；已安装的生产服务
未被停止或改动。凭据只存在于子进程环境变量，未落文件、日志、证据或 Git。

## 第一次运行：车载出厂配置

`JourneyRuntime:enabled=true`、`RiotCreateDispatch:enabled=false`。

五步恢复成功，`SessionRecoveries` 稳定为 `Ready / READY`，`DepartureSafe=1`，
session generation 1，capability revision 1，safety revision 2，`/health/ready` 为 HTTP 200。
这是本票首次在真实双端下取得会话就绪，此前各次均 fail-close 在
`DEPARTURE_SAFETY_NOT_READY`。

但没有任何 Demand 被受理：WIRE_TO_GATE 12 条中 7 条 `OUT_OF_SCOPE_AREA`、4 条
`ONBOARD_FACTS_NOT_READY`、1 条 `AREA_STATION_NOT_FOUND`。根因见下方发现一。

## 第二次运行：能力覆盖探测

为定位阻断，在一次性克隆的车载配置中把 `wireToGate.supportsBatchUnlock` 由 `false` 改为
`true`，其余不变。**该覆盖只用于诊断，不代表车载实现真的支持批量解锁**，因此本次结果不得
作为任何切片的 G3 或验收证据。

结果：一条真实 WIRE_TO_GATE Demand 完成受理，产生唯一 `AcceptedDemand`、`OrderIntent`、
`JourneyRuntime` 与 `VehicleDispatchLease`；另有 3 条 `ELIGIBLE`（受单车排他租约约束未受理）。

旅程推进到 `Stage=AwaitingPickupArrival`，并稳定停在：

```
BlockReasonCode = PICKUP_CreateDispatchDisabled
```

## 关闭态不消耗建单资格（运行态证明）

`upperId = W2G-94993971b3624edf81bc712d160e444a-PICKUP-1`：

| 字段 | 值 |
| --- | --- |
| `Status` | `PENDING_RECONCILIATION` |
| `OrderId` | null |
| `CreateAttemptCount` | 0 |
| `CreateAttemptId` | null |
| `DispatchAuditVersion` | 1 |
| `DispatchAuditSequence` | 0 |
| `LastReconciliationOutcome` | null |
| `RiotDispatchAuditEvents` 总行数 | 0 |

审计链一行未写，intent 状态与 at-most-once 计数器完全未动，因此该 intent 仍完整保留后续
授权建单的资格。这把此前只有单元测试覆盖的不变量提升为真机运行态证据。

车辆租约 `BROKERX-0c20ff0600d644869a6a80c186065d85` 已建立且 `ReleasedAt` 为空，符合单车
单 Demand 排他语义。

两次运行的 stderr 均为空，58105／58107／1502／58006 四个端口全部回收，无 RIoT mutation、
无订单、无车辆移动。

## 发现一：`supportsBatchUnlock=false` 阻断全部受理

`JourneyRuntimeEngine.ReadOnboardFactsAsync` 在
`!capabilityPayload.GetProperty("supportsBatchUnlock").GetBoolean()` 时返回 null，随后
`ValidateDynamicFacts` 一律判为 `ONBOARD_FACTS_NOT_READY`。车载出厂 `appsettings.json` 中
`wireToGate.supportsBatchUnlock` 为 `false`，其发出的 `CapabilitySnapshot` 亦为 `false`，
故在该配置下没有任何 WIRE_TO_GATE Demand 能通过动态门禁。

该配置由 `8005-agv-onboard-hmi` 拥有，对 agent 只读，本仓库不写入其问题记录或修复。

## 发现二：快照发送节奏与 30 秒新鲜度要求不相容

观测事实：两次运行中 `CapabilitySnapshot` 与 `SafetyStateSnapshot` 各只在会话建立时发送
**一次**，此后仅有周期 `Heartbeat`（第一次运行 2 分钟内 23 个，第二次 1 分钟内 11 个），
未再重发任何快照。

契约事实：`ReadOnboardFactsAsync` 要求两个快照的 `observedAt` 都新于
`JourneyRuntime:maximumEvidenceAge`，当前为 30 秒。

由此推论：一个会话的受理窗口只有建立后的前 30 秒。第二次运行的受理发生在快照后
**2.4 秒**（快照 `16:44:16.886`／`16:44:16.930`，受理 `16:44:19.305`），恰好落在窗口内；
会话持续到 `16:45:12` 期间不再有新快照，此后该会话已无法再受理任何 Demand。

该问题跨越两端：发送节奏属车载，新鲜度阈值属 ControlServer。归属需由用户指定可写目标仓库
后才能写入完整缺陷记录，本仓库不单方面认定归属。

## 未证明的事项

本次只证明关闭态的拒绝分支与资格保全。发现一的覆盖使第二次运行不具备验收效力；真实建单、
移动闭环、现场物理安全确认与逐次授权均未进行。W2G-IS-00～07 的正式 G3 与 RC 保持
`INCONCLUSIVE`。
