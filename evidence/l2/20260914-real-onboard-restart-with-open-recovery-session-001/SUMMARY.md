# L2 场景证据：real-onboard-restart-with-open-recovery-session

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T085457826Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `2b2aa51cd57f1eff0e76158eaab7fc8687e3f5a7` |
| onboardHmiCommit | `b96010825d43aeee3b861cb3b4716f4d0873c8a0` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T085457826Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked/LOAD_RESULT_REQUIRES_RECOVERY` |
| 重启前服务端有一个开着的恢复会话：SessionRequested → Opened；RESUME_AFTER_REPAIR 因没有已证实断点被拒（PROVEN_RECOVERY_CHECKPOINT_REQUIRED）；这辆车只有这一个会话行，OPEN、未选动作、revision 1、没有工作流 | PASS | `Opened / RESUME_AFTER_REPAIR→RecoveryActionRejected(PROVEN_RECOVERY_CHECKPOINT_REQUIRED) / 会话行 1：OPEN、无动作、r1 / 工作流 0` | `ExceptionRecoverySessionOpened / RESUME_AFTER_REPAIR→RecoveryActionRejected(PROVEN_RECOVERY_CHECKPOINT_REQUIRED) / 会话行 1：OPEN、动作=''、r1 / 工作流 0` |
| 重启前那份 OPEN 快照可重放：这个会话在发件箱只有一份快照，OPEN、r1、允许 COMPENSATE_LOAD_ALL_EMPTY、绑在当前会话代次，未确认、未 fence | PASS | `1 份：r1 OPEN g1 ack=False fenced=False，允许动作含 COMPENSATE_LOAD_ALL_EMPTY` | `1 份：r1 OPEN g1 ack=False fenced=False，允许动作 RESUME_AFTER_REPAIR,COMPENSATE_LOAD_ALL_EMPTY,FAULT_CARGO_HANDOFF,FORCED_MECHANICAL_RECOVERY` |
| 服务端在重启后的恢复状态报告之后把那份 OPEN 快照重放进新会话：同一 messageId 的发件箱行改绑到新代次，载荷不变（仍 OPEN r1），仍未确认、未 fence，没有另起快照（ReplayPendingCommandsAsync） | PASS | `RecoveryStateReport g>1 在重启之后 / 同一行 g=报告代次、r1 OPEN、载荷相同、ack=False fenced=False / 快照 1 份` | `RecoveryStateReport g2 重启后=True / 同一行 g2 r1 OPEN 载荷相同=True ack=False fenced=False / 快照 1 份` |
| 重启后的恢复入口可用：第一次按「补偿清空」就走到补偿结果（没有「补偿清空失败」即没有 RECOVERY_SESSION_STATE_PENDING 一类的拒绝）；重启后没有再申请会话、没有 ALREADY_OPEN 一类的会话拒绝；COMPENSATE_LOAD_ALL_EMPTY 挂在重启前那个会话上、在新代次里被 Accepted；这辆车始终只有一个恢复会话 | FAIL | `第一次按下即有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1 / COMPENSATE×1 → Accepted，会话 49264f0e-00f7-3d51-a883-80e62b3b53ad，g≥2` | `第一次按下：REFUSED；再按一次：REFUSED / 重启后 SessionRequested 0 / 会话行 1 / COMPENSATE×0` |
| 未到达：重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED） | FAIL | `(reached)` | `(not reached) 重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED）` |
| 未到达：重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED） | FAIL | `(reached)` | `(not reached) 重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED）` |
| 未到达：重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED） | FAIL | `(reached)` | `(not reached) 重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
