# L2 场景证据：real-onboard-restart-with-open-recovery-session

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T053607459Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35689381489-1\_stage\l2-20260922T053607459Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked/LOAD_RESULT_REQUIRES_RECOVERY` |
| 重启前服务端有一个开着的恢复会话：SessionRequested → Opened，这条应答被代理丢掉（车放弃申请之后才判）；重启前没有为这笔需求提交过任何恢复动作；这辆车只有这一个会话行，OPEN、未选动作、revision 1、没有工作流 | PASS | `Opened（应答被丢）/ 恢复动作 0 / 会话行 1：OPEN、无动作、r1 / 工作流 0` | `ExceptionRecoverySessionOpened（应答被丢）/ 恢复动作 0 / 会话行 1：OPEN、动作=''、r1 / 工作流 0` |
| 重启前那份 OPEN 快照可重放：这个会话在发件箱只有一份快照，OPEN、r1、允许 COMPENSATE_LOAD_ALL_EMPTY、绑在当前会话代次，未确认、未 fence | PASS | `1 份：r1 OPEN g2 ack=False fenced=False，允许动作含 COMPENSATE_LOAD_ALL_EMPTY` | `1 份：r1 OPEN g2 ack=False fenced=False，允许动作 RESUME_AFTER_REPAIR,COMPENSATE_LOAD_ALL_EMPTY,FAULT_CARGO_HANDOFF,FORCED_MECHANICAL_RECOVERY` |
| 服务端在重启后的恢复状态报告之后把那份 OPEN 快照重放进新会话：同一 messageId 的发件箱行改绑到新代次，载荷不变（仍 OPEN r1），仍未确认、未 fence，没有另起快照（ReplayPendingCommandsAsync） | PASS | `RecoveryStateReport g>2 在重启之后 / 同一行 g=报告代次、r1 OPEN、载荷相同、ack=False fenced=False / 快照 1 份` | `RecoveryStateReport g3 重启后=True / 同一行 g3 r1 OPEN 载荷相同=True ack=False fenced=False / 快照 1 份` |
| 重启后的恢复入口可用：第一次按「补偿清空」就走到补偿结果（没有「补偿清空失败」即没有 RECOVERY_SESSION_STATE_PENDING 一类的拒绝）；重启后没有再申请会话、没有 ALREADY_OPEN 一类的会话拒绝；COMPENSATE_LOAD_ALL_EMPTY 挂在重启前那个会话上、在新代次里被 Accepted；这辆车始终只有一个恢复会话 | PASS | `第一次按下即有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1 / COMPENSATE×1 → Accepted，会话 d07c90ee-e210-b852-ad7c-0b51e6f015de，g≥3` | `第一次按下：有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1 / COMPENSATE×1 → RecoveryActionAccepted，会话 d07c90ee-e210-b852-ad7c-0b51e6f015de，g3` |
| 补偿在重启后的新会话里按向量走完，各一次：ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted → LoadCompensationRequested → LoadCompensationCommand（指向重启前那个会话、这笔装载与装载仓）→ LoadCompensationResult(ALL_EMPTY) → DurableAck；同一 recoveryActionId 下没有别的动作（一致性检查：重启前没有发过动作，本场景恒为真，不证 id 复用） | PASS | `各 1，重启之后按序，代次 ≥ 3，命令会话 d07c90ee-e210-b852-ad7c-0b51e6f015de / attempt bf1b9828-15d4-fe52-b5aa-dae23cfaf66e / 仓 1，ALL_EMPTY；同 id 其它动作 无` | `同 id 其它动作：无 / Action×1 / CompensationRequested×1 / Command×1 / Result×1 ALL_EMPTY→DurableAck / 命令会话 d07c90ee-e210-b852-ad7c-0b51e6f015de 仓 1 / 有序=True` |
| 补偿走到对账且车回到 Ready：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程 Completed/CANCELLED_BY_LOAD_COMPENSATION、会话 Readiness=Ready；仓位关门、空、锁上、输出复位，补偿没有开锁 | PASS | `Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready / 1=CLOSED/EMPTY/1/0 / 开锁 0` | `Reconciled / CLOSED / Cancelled / Cancelled / 释放=True / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready(READY) / 1=CLOSED/EMPTY/1/0 / 开锁 0` |
| 开着的快照从没被确认：这个会话每一份非 CLOSED 快照（含重启前那份 OPEN r1）都未确认，并由下一 revision fence；最后恰有一份 CLOSED。CLOSED 是否被确认只抄在实际值里，不作判据（车载端只确认 CLOSED，control-server#31） | PASS | `非 CLOSED 全部 ack=False fenced=True，OPEN r1 在其中，最后一份 CLOSED` | `r1 OPEN g3 ack=False fenced=True → r2 ACTION_SELECTED g3 ack=False fenced=True → r3 EXECUTING g3 ack=False fenced=True → r4 CLOSED g3 ack=True fenced=False（CLOSED ack=True）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
