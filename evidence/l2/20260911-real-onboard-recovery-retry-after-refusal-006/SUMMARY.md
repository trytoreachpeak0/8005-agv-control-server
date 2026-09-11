# L2 场景证据：real-onboard-recovery-retry-after-refusal

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T111830513Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `6af64ab5d6cd8089a38381c508d24b78ea9ddf85` |
| onboardHmiCommit | `ab346ed4b15d86110429273014d8a9be034bdab1` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T111830513Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车报回真的 UNKNOWN、仓位操作 RecoveryRequired，而引擎停摆让旅程停在 AwaitingLoadResult 转不了 Blocked | PASS | `RecoveryRequired / AwaitingLoadResult` | `RecoveryRequired / AwaitingLoadResult` |
| 旅程还没 Blocked 时的补偿清空被服务端拒绝，原因码原样回到自动化面 | PASS | `409 / RECOVERY_DEMAND_NOT_BLOCKED` | `409 / RECOVERY_DEMAND_NOT_BLOCKED` |
| 引擎恢复之后旅程转 Blocked / LOAD_RESULT_REQUIRES_RECOVERY | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 同一个 attempt 被拒过之后再请求，服务端对账 Reconciled、结论 ALL_EMPTY，需求判 Cancelled | PASS | `Reconciled / ALL_EMPTY / Cancelled` | `Reconciled / ALL_EMPTY / Cancelled` |
| 两次按下是两条 messageId 不同的会话请求，各自拿到自己的应答：先拒绝、后开出会话 | PASS | `2 条 / 2 个 id / ExceptionRecoverySessionRejected RECOVERY_DEMAND_NOT_BLOCKED → ExceptionRecoverySessionOpened` | `2 条 / 2 个 id / ExceptionRecoverySessionRejected RECOVERY_DEMAND_NOT_BLOCKED → ExceptionRecoverySessionOpened` |
| 从第一次按下到收尾，车载端没有重连——服务端没有因为内容冲突掐连接 | PASS | `SessionHello 1 条不变` | `SessionHello 1 → 1 条` |
| 单需求旅程以补偿收尾，现场收在安全状态：门关、仓空、已锁、开锁输出复位 | PASS | `Completed / CLOSED/EMPTY/1/0` | `Completed / CLOSED/EMPTY/1/0` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 39 lines, 39 distinct, 14 message types, 0 distinct violations, 0 known; schema compilation 19400 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
