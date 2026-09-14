# L2 场景证据：real-onboard-restart-with-open-recovery-session

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T100003975Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `8bf65925e0096ddc3834c9483914252d8f6f5148` |
| onboardHmiCommit | `19a740c5cc0ae691fa4d8e5f20fd433808acedb9` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| protocolReleaseIdentity.schemaBundleSha256 | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T100003975Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked/LOAD_RESULT_REQUIRES_RECOVERY` |
| 重启前服务端有一个开着的恢复会话：SessionRequested → Opened；RESUME_AFTER_REPAIR 因没有已证实断点被拒（PROVEN_RECOVERY_CHECKPOINT_REQUIRED）；这辆车只有这一个会话行，OPEN、未选动作、revision 1、没有工作流 | PASS | `Opened / RESUME_AFTER_REPAIR→RecoveryActionRejected(PROVEN_RECOVERY_CHECKPOINT_REQUIRED) / 会话行 1：OPEN、无动作、r1 / 工作流 0` | `ExceptionRecoverySessionOpened / RESUME_AFTER_REPAIR→RecoveryActionRejected(PROVEN_RECOVERY_CHECKPOINT_REQUIRED) / 会话行 1：OPEN、动作=''、r1 / 工作流 0` |
| 重启前那份 OPEN 快照可重放：这个会话在发件箱只有一份快照，OPEN、r1、允许 COMPENSATE_LOAD_ALL_EMPTY、绑在当前会话代次，未确认、未 fence | PASS | `1 份：r1 OPEN g1 ack=False fenced=False，允许动作含 COMPENSATE_LOAD_ALL_EMPTY` | `1 份：r1 OPEN g1 ack=False fenced=False，允许动作 RESUME_AFTER_REPAIR,COMPENSATE_LOAD_ALL_EMPTY,FAULT_CARGO_HANDOFF,FORCED_MECHANICAL_RECOVERY` |
| 服务端在重启后的恢复状态报告之后把那份 OPEN 快照重放进新会话：同一 messageId 的发件箱行改绑到新代次，载荷不变（仍 OPEN r1），仍未确认、未 fence，没有另起快照（ReplayPendingCommandsAsync） | PASS | `RecoveryStateReport g>1 在重启之后 / 同一行 g=报告代次、r1 OPEN、载荷相同、ack=False fenced=False / 快照 1 份` | `RecoveryStateReport g2 重启后=True / 同一行 g2 r1 OPEN 载荷相同=True ack=False fenced=False / 快照 1 份` |
| 重启后的恢复入口可用：第一次按「补偿清空」就走到补偿结果（没有「补偿清空失败」即没有 RECOVERY_SESSION_STATE_PENDING 一类的拒绝）；重启后没有再申请会话、没有 ALREADY_OPEN 一类的会话拒绝；COMPENSATE_LOAD_ALL_EMPTY 挂在重启前那个会话上、在新代次里被 Accepted；这辆车始终只有一个恢复会话 | PASS | `第一次按下即有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1 / COMPENSATE×1 → Accepted，会话 b2b2d977-4037-235c-afce-72edde819303，g≥2` | `第一次按下：有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1 / COMPENSATE×1 → RecoveryActionAccepted，会话 b2b2d977-4037-235c-afce-72edde819303，g2` |
| 补偿在重启后的新会话里按向量走完，各一次：ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted → LoadCompensationRequested → LoadCompensationCommand（指向重启前那个会话、这笔装载与装载仓）→ LoadCompensationResult(ALL_EMPTY) → DurableAck；同一 recoveryActionId 下别的动作只能是重启前被拒的那次 | PASS | `各 1，重启之后按序，代次 ≥ 2，命令会话 b2b2d977-4037-235c-afce-72edde819303 / attempt 870db750-cc11-a058-89bb-dcda38d85ff0 / 仓 1，ALL_EMPTY；同 id 其它动作全是重启前的 RecoveryActionRejected` | `同 id 其它动作：RESUME_AFTER_REPAIR→RecoveryActionRejected(g1，重启前=True) / Action×1 / CompensationRequested×1 / Command×1 / Result×1 ALL_EMPTY→DurableAck / 命令会话 b2b2d977-4037-235c-afce-72edde819303 仓 1 / 有序=True` |
| 补偿走到对账且车回到 Ready：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程 Completed/CANCELLED_BY_LOAD_COMPENSATION、会话 Readiness=Ready；仓位关门、空、锁上、输出复位，补偿没有开锁 | PASS | `Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready / 1=CLOSED/EMPTY/1/0 / 开锁 0` | `Reconciled / CLOSED / Cancelled / Cancelled / 释放=True / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready(READY) / 1=CLOSED/EMPTY/1/0 / 开锁 0` |
| 开着的快照从没被确认：这个会话每一份非 CLOSED 快照（含重启前那份 OPEN r1）都未确认，并由下一 revision fence；最后恰有一份 CLOSED。CLOSED 是否被确认只抄在实际值里，不作判据（v2 车载端未带 onboard-hmi#41/#42） | PASS | `非 CLOSED 全部 ack=False fenced=True，OPEN r1 在其中，最后一份 CLOSED` | `r1 OPEN g2 ack=False fenced=True → r2 ACTION_SELECTED g2 ack=False fenced=True → r3 EXECUTING g2 ack=False fenced=True → r4 CLOSED g2 ack=False fenced=False（CLOSED ack=False）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
