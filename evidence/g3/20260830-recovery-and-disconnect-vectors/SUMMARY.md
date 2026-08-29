# staged G3 断联安全收尾与恢复分支：`3d8b00c` + `304e6ad`

## 运行类型

`STAGED_G3_REAL_PEERS_DETERMINISTIC_TLS`。loopback 隔离，**不动车、不建单、不使用任何现场凭据**。
用户明确授权安装唯一临时测试根（`CurrentUser/Root`）。

## 结论

十九条断言全部 PASS（原十二条不回归，新增七条）：

| 断言 | 结果 | 新增 |
| --- | --- | --- |
| `identityRejections` | PASS | |
| `sameConnectionSameMessageIdSameContent` | PASS | |
| `sameMessageIdDifferentContentStableConflict` | PASS | |
| `recoveryStateReportFirstAckDropReplay` | PASS | |
| `recoveryStateReportFirstAckDropReplayOverTls` | PASS | |
| `businessMessageSameMessageIdSameContentReplay` | PASS | |
| `businessMessageSameMessageIdDifferentContentStableConflict` | PASS | |
| `businessMessageAckDropInSessionReplay` | PASS | |
| `businessMessageDelayedDeliveryAccepted` | PASS | |
| `businessMessageReorderedDeliveryAccepted` | PASS | |
| `recoverySessionAuthorisationBoundary` | PASS | ✅ |
| `recoveryActionsRefusedWithoutPersistedOperation` | PASS | ✅ |
| `hardwareRecoveryRecordScopeEnforced` | PASS | ✅ |
| `recoveryCommandSurvivesMidFlightDisconnect` | PASS | ✅ |
| `forcedRecoveryGenerationAdvancesMonotonically` | PASS | ✅ |
| `supersededGenerationResultIsHistoricalEvidenceOnly` | PASS | ✅ |
| `recoveryNeverReportsFalseCompletion` | PASS | ✅ |
| `noMovementOrExternalSideEffects` | PASS | |
| `secretScan` | PASS | |

`status = STAGED_G3_TLS_RECOVERY_REPLAY_PASS`，`formalSlicePass = false`。
运行 `20260829T164823019Z`，耗时 4 分 57 秒，`configurationSha256`
`3332bd06501c45433df7d3875df6e49ef1e36c152ed6087f3e9dbfa00d36d528`。

## 绑定身份

| 组件 | commit |
| --- | --- |
| ControlServer | `3d8b00c7558ae700358f1f995a5ac75d12a3250c` |
| OnboardHmi | `304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6` |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| protocol | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`（`protocol-v0.1.1`，G1 `PASS`）|
| runner／harness | `e0d4da3`，运行开始时工作树干净 |

manifest `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`，
ControlServer `/version` 回读 `approvalStatus = APPROVED_RELEASE`。

同一份 harness 曾以未提交状态先跑过一次并全绿，但那次 `commits.harness` 只能报出上一个
commit `6a678a1`——一个并不含本次探针的身份——`harnessWorktreeCleanAtStart` 随之为 `false`。
该次证据已作废删除；本目录是提交 `e0d4da3` 之后的重跑，身份字段可直接采信。

## 本次新增了什么

票据 18 把故障注入泛化到了业务消息面，但恢复面（请求与结果）从未由合成对端驱动过，
因此「断联安全收尾」与「恢复分支」两类向量无处运行。本次新增 `RunRecoveryProbeAsync`，
一个合成恢复对端，覆盖八种恢复消息：

`ExceptionRecoverySessionRequested`、`RecoveryActionSubmitted`、`HardwareRecoveryRecordSubmitted`、
`LoadCancellationStartRequested`、`LoadCompensationRequested`、`LoadCorrectionRequested`、
`ForcedMechanicalRecoveryResult`、`ForcedMechanicalRecoveryCommand`（服务端出向）。

管理员凭证（`CONTROL_SERVER_RECOVERY_AUTHENTICATION_PROOF`）每次运行现生成注入服务端环境，
入库时由 `RedactRecoveryAuthenticationProof` 抹除，并已加入证据 secret scan——本次 `secretScan = PASS`，
`secretLeakFiles` 为空。

### 会话授权边界（六项）

| 向量 | 观测 |
| --- | --- |
| 凭证不匹配 | `ExceptionRecoverySessionRejected` / `RECOVERY_AUTHENTICATION_REQUIRED` |
| 指向未受理 demand | `ExceptionRecoverySessionRejected` / `RECOVERY_DEMAND_NOT_BLOCKED` |
| 无 demand 开会话 | `ExceptionRecoverySessionOpened` |
| 同 requestId 同内容、换 messageId 重发 | 再次 `Opened`，`exceptionRecoverySessionId` 与 `recoverySessionRevision` 均不变 |
| 会话开启中再请求 | `RECOVERY_SESSION_ALREADY_OPEN` |
| 同 requestId 不同内容 | 连接被关闭（内容冲突）|

第四项刻意换了 messageId：同 messageId 只会命中 inbox 的首响应重放，换 messageId 才会走到
`OpenSessionAsync` 自己的 requestId 幂等分支。

### 恢复动作与装载族的拒绝边界（七项）

`RESUME_AFTER_REPAIR`、`COMPENSATE_LOAD_ALL_EMPTY`、`FAULT_CARGO_HANDOFF` 三种动作在无
demand 的会话上均返回 `RecoveryActionRejected` / `ACTION_NOT_ALLOWED_IN_STATE`；仓位集合不符时
返回 `RECOVERY_SCOPE_MISMATCH`。`LoadCancellationStartRequested` 返回
`LoadCancellationAuthorization` 且 `decision = REJECTED`，`LoadCorrectionRequested` 与
`LoadCompensationRequested` 分别返回各自的 `*Rejected`。

这七项不是「凑数的负向用例」：**服务端拒绝为一个自己从未受理过的 demand 授权任何恢复动作**
本身就是安全属性，也正好把不可达的那一半在证据里划出明确边界。

### 断联安全收尾

`FORCED_MECHANICAL_RECOVERY` 是唯一无需 demand 即可被接受的动作，因此由它承载需要真实
workflow 的向量。代理对服务端出向的 `ForcedMechanicalRecoveryCommand` 注入 `drop-and-close`：

```
droppedCommandMessageId          6d8081bc-4033-5858-aecc-be278dd920f5
droppedCommandSessionGeneration  3
replayedCommandSessionGeneration 4
```

合成对端收到 `RecoveryActionAccepted` 之后连接即断，命令一个字节也没到达。重连并发出
`RecoveryStateReport` 后，`ReplayPendingCommandsAsync` 从同一条 outbox 行重发了**同一个
messageId** 的命令，`recoveryActionId` 与 `forcedRecoveryGeneration` 均未变。

线上字节不相等（`9f343354…` → `789c74a9…`）是**预期行为**，不是缺陷：
`ReplayPendingForSessionAsync` 只重写 `sessionGeneration`，`sentAt` 仍冻结在 `row.CreatedAt`。
判据因此取自身份而非字节：`ProtocolOutbox` 中 `ForcedMechanicalRecoveryCommand` 仅两行
（两次动作各一行），`rowCount` 均为 1——断联没有制造第二条命令。

### 恢复分支与代际

两次 `FORCED_MECHANICAL_RECOVERY` 之间插入一次报告新代的 `RecoveryStateReport`
（否则 `ReportedForcedRecoveryGeneration != ForcedRecoveryGeneration` 会直接拒绝），
`VehicleRecoveryGenerations` 从 1 单调推进到 2。随后：

| 结果消息 | 代 | workflow 终态 | 证据行 |
| --- | --- | --- | --- |
| 指向第一次动作 | 1（已被 2 取代）| `HistoricalOnly` | `historicalOnly = true` |
| 指向第二次动作 | 2（当前）| `RecoveryRequired` | `historicalOnly = false` |

第二条走的是成功分支（`MECHANICALLY_ISOLATED`、`electronicEmptyProven = false`、
`vehicleReadyProven = false`），**服务端仍把 workflow 置为 `RecoveryRequired` 而非
`Reconciled`**——强制机械恢复是隔离，不是完成，这正是本轮最想钉住的一条。

同 messageId 同内容重发得到逐字节相同的 `DurableAck`（`da402a3d…`）；同 messageId 换
`observedAt` 则连接被关闭。

### 不误报完成

| 事实 | 值 |
| --- | --- |
| `ExceptionRecoverySessions` 行数 | 1（`EXECUTING`，`demandId` 为空）|
| `State = 'CLOSED'` 的会话数 | 0 |
| `State = 'Reconciled'` 的 workflow 数 | 0 |
| 带 `demandId` 或 `slotOperationAttemptId` 的 workflow 数 | 0 |
| `HardwareRecoveryRecords` | 1（越界的第二条被拒，未落库）|
| `OrderIntents` / `AcceptedDemands` / `StationOperations` | 0 / 0 / 0 |

## 未覆盖的三类，及原因

写入 `run-result.json` 的 `recoveryProbe.coverageLimits`，不以任何方式暗示已覆盖：

- **仓位操作进行中断开**：需要一行处于 `RecoveryRequired` 的 `StationOperations`，
  而它只由 `PrepareSlotOperationAsync` 写入，需来自 MesIngest 与 RIoT 的已受理 demand。
  断联向量因此落在 `ForcedMechanicalRecoveryCommand` 上——唯一无需 demand 的已授权恢复命令。
- **resume／compensation／correction／cancellation／fault-cargo 的接受路径**：同样需要
  持久化的 `StationOperations` 行，`RESUME_AFTER_REPAIR` 还额外需要会话上的
  `ProvenRecoveryCheckpoint`。staged 运行只能证明它们在无 demand 时被拒绝，不能证明它们在
  有 demand 时完成。
- **RIoT UNKNOWN 对账**：RIoT 指向死端口且不存在 demand，不会发生任何建单尝试，也就无从产生
  UNKNOWN。该行为记录在 `evidence/g3/20260829-authorized-single-real-create/` 的审计链中，
  要成为断言向量仍需一次对接真实 RIoT 的带 demand 运行。

**没有捏造任何状态**。为凑覆盖率而伪造一行 `StationOperations` 会同时抽空
`noMovementOrExternalSideEffects` 的含义，这一权衡在票据 19 中已明确否决。

## 断言的可证伪性

绿断言在被证明会变红之前不构成证据。正式运行前，runner 的数据库观测段与恢复断言段被原样抽出，
喂以本机一次等价运行的真实产物，逐条施加单点变异：

| 变异 | 应被哪条断言发现 | 结果 |
| --- | --- | --- |
| 车辆代际停在 1 | `forcedRecoveryGenerationAdvancesMonotonically` | 检出 |
| 旧代结果被当作当前代 | 同上 | 检出 |
| 会话报告为 `CLOSED` | `recoveryNeverReportsFalseCompletion` | 检出 |
| 出现 `Reconciled` 的 workflow | 同上 | 检出 |
| 命令从未被丢弃 | `recoveryCommandSurvivesMidFlightDisconnect` | 检出 |
| 命令在同一会话代重放 | 同上 | 检出 |
| 出现重复的命令 outbox 行 | 同上 | 检出 |
| 多出一条硬件恢复记录 | `hardwareRecoveryRecordScopeEnforced` | 检出 |

八条全部检出，还原后仍全绿。

## 运行后独立复核

不采信 `temporaryTrustCleanupVerified` 的自报，三项均独立查过：

- `CurrentUser\Root` 中 `CN=8005 staged G3*` 证书数 = 0
- 58205／58207／58215／58216／1502／58006 均无 LISTEN
- 无 `SQCD*` 残留进程；`ControlServer.Host` PID 32544 是已安装服务，未占上述端口

## 分级

`formalSlicePass = false`，`W2G-IS-00` 与 `W2G-IS-06` 仍为 `INCONCLUSIVE`，
`fullG3` 与 `releaseCandidate` 仍为 `INCONCLUSIVE`。本运行只解决票据 19 要求的
「断联安全收尾」与「恢复分支」在 staged 可达面上的取证，不构成正式切片通过。
