# L2 场景证据：real-onboard-restart-after-recovery-session-opened

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T053653559Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `f1286c29aa0cbeffdd750f3016a52590dd4659b9` |
| onboardHmiCommit | `ecdb3a0be1d95ef51e7d40493808ba4659274f41` |
| protocolFaultProxy | `True` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35689381489-1\_stage\l2-20260922T053653559Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked/LOAD_RESULT_REQUIRES_RECOVERY` |
| 重启前会话开成、动作已受理、受理应答被丢：SessionRequested → Opened 送达，ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted 而这条应答被代理按 messageId 丢掉（车放弃按下之后才判）；这辆车只有这一个会话行，ACTION_SELECTED、选中补偿、revision 2；工作流恰一个 AwaitingAuthorization 且还没发出补偿命令 | PASS | `会话行 1：ACTION_SELECTED、COMPENSATE_LOAD_ALL_EMPTY、r2 / 工作流 1：AwaitingAuthorization、无命令 / 补偿命令 0` | `会话行 1：ACTION_SELECTED、动作='COMPENSATE_LOAD_ALL_EMPTY'、r2 / 工作流 1：COMPENSATE_LOAD_ALL_EMPTY、AwaitingAuthorization、命令='' / 补偿命令 0` |
| 断电时车载端自己已经记着这次会话：日志库 WireToGateRecoveryState 的 exceptionRecoverySessionId 等于服务端开出的会话 id，动作向量与动作 id 也已落盘。这是本场景唯一读车载端库的判据——要证的那一格（车记着会话、内存里没有快照）就在它的持久状态里 | PASS | `会话 '7e04008b-100b-315a-8838-79699816198c'、动作 id 非空、向量 LOAD_COMPENSATION` | `会话 '7e04008b-100b-315a-8838-79699816198c'、动作 'cbef1214-d736-a35c-b737-b744eacb4111'、向量 LOAD_COMPENSATION/cbef1214-d736-a35c-b737-b744eacb4111` |
| 重启前有一份可重放的快照：这个会话在发件箱里最后一份既未确认也未 fence，ACTION_SELECTED、r2、选中 COMPENSATE_LOAD_ALL_EMPTY、绑在当前会话代次；更早的 OPEN r1 已被它 fence | PASS | `待重放恰 1 份：r2 ACTION_SELECTED/COMPENSATE_LOAD_ALL_EMPTY g2；r1 已 fence` | `2 份：r1 OPEN/- g2 ack=False fenced=True → r2 ACTION_SELECTED/COMPENSATE_LOAD_ALL_EMPTY g2 ack=False fenced=False` |
| 服务端在重启后的恢复状态报告之后把那份快照重放进新会话：同一 messageId 的发件箱行改绑到新代次，载荷不变（仍 ACTION_SELECTED r2），仍未确认、未 fence，没有另起快照（ReplayPendingCommandsAsync） | PASS | `RecoveryStateReport g>2 在重启之后 / 同一行 g=报告代次、r2 ACTION_SELECTED、载荷相同、ack=False fenced=False / 快照仍 2 份` | `RecoveryStateReport g3 重启后=True / 同一行 g3 r2 ACTION_SELECTED 载荷相同=True ack=False fenced=False / 快照 2 份` |
| 重启后的恢复入口可用：第一次按「补偿清空」就走到补偿结果（没有「补偿清空失败」，即车载端没有在本地抛 RECOVERY_SESSION_STATE_PENDING）；重启后没有再申请会话；这辆车始终只有一个恢复会话行，还是重启前那一个 | PASS | `第一次按下即有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1，仍是 7e04008b-100b-315a-8838-79699816198c` | `第一次按下：有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1：7e04008b-100b-315a-8838-79699816198c` |
| 动作只被提交过一次，补偿在重启后的新会话里走完：这个会话下 RecoveryActionSubmitted 恰一条、动作是 COMPENSATE_LOAD_ALL_EMPTY、发生在重启之前，重启之后不再提交；随后 LoadCompensationRequested → LoadCompensationCommand（指向这个会话、这笔装载与装载仓）→ LoadCompensationResult(ALL_EMPTY) → DurableAck 各一次、按序、都在重启之后、代次 ≥ 重启后的报告代次 | PASS | `ActionSubmitted×1（重启前，COMPENSATE_LOAD_ALL_EMPTY，id cbef1214-d736-a35c-b737-b744eacb4111）/ 重启后 CompensationRequested、Command、Result 各 1，按序，代次 ≥ 3，命令会话 7e04008b-100b-315a-8838-79699816198c / attempt 11b7e05d-add3-4652-9e95-4c31cad17096 / 仓 1，ALL_EMPTY` | `ActionSubmitted×1（重启前=True，id cbef1214-d736-a35c-b737-b744eacb4111→RecoveryActionAccepted） / CompensationRequested×1 / Command×1 / Result×1 ALL_EMPTY→DurableAck / 命令会话 7e04008b-100b-315a-8838-79699816198c 仓 1 / 有序=True` |
| 补偿走到对账且车回到 Ready：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程 Completed/CANCELLED_BY_LOAD_COMPENSATION、会话 Readiness=Ready；仓位关门、空、锁上、输出复位，补偿没有开锁。口径与 g3-exception-compensate、real-onboard-restart-with-open-recovery-session 相同 | PASS | `Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready / 1=CLOSED/EMPTY/1/0 / 开锁 0` | `Reconciled / CLOSED / Cancelled / Cancelled / 释放=True / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready(READY) / 1=CLOSED/EMPTY/1/0 / 开锁 0` |
| 开着的快照从没被确认：这个会话每一份非 CLOSED 快照（含重放回来那份 ACTION_SELECTED r2）都未确认，并由下一 revision fence；最后恰有一份 CLOSED。CLOSED 是否被确认只抄在实际值里，不作判据（车载端只确认 CLOSED，control-server#31） | PASS | `非 CLOSED 全部 ack=False fenced=True，重放那份 ACTION_SELECTED r2 在其中，最后一份 CLOSED` | `r1 OPEN/- g2 ack=False fenced=True → r2 ACTION_SELECTED/COMPENSATE_LOAD_ALL_EMPTY g3 ack=False fenced=True → r3 EXECUTING/COMPENSATE_LOAD_ALL_EMPTY g3 ack=False fenced=True → r4 CLOSED/COMPENSATE_LOAD_ALL_EMPTY g3 ack=True fenced=False（CLOSED ack=True）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
