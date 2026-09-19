# L2 场景证据：real-onboard-restart-with-open-recovery-session

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T163847772Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `e5ef8e44546df85a5252a8e0dcd5e77661ebf46c` |
| onboardHmiCommit | `4d716340982de4e39339c2151c291efe1a21e1d1` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35455541316-1\_stage\l2-20260919T163847772Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked/LOAD_RESULT_REQUIRES_RECOVERY` |
| 重启前服务端有一个开着的恢复会话：SessionRequested → Opened；RESUME_AFTER_REPAIR 因没有已证实断点被拒（PROVEN_RECOVERY_CHECKPOINT_REQUIRED）；这辆车只有这一个会话行，OPEN、未选动作、revision 1、没有工作流 | FAIL | `Opened / RESUME_AFTER_REPAIR→RecoveryActionRejected(PROVEN_RECOVERY_CHECKPOINT_REQUIRED) / 会话行 1：OPEN、无动作、r1 / 工作流 0` | `ExceptionRecoverySessionOpened / RESUME_AFTER_REPAIR→RecoveryActionAccepted() / 会话行 1：CLOSED、动作='RESUME_AFTER_REPAIR'、r3 / 工作流 1` |
| 未到达：重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格 | FAIL | `(reached)` | `(not reached) 重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格` |
| 未到达：重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格 | FAIL | `(reached)` | `(not reached) 重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格` |
| 未到达：重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格 | FAIL | `(reached)` | `(not reached) 重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格` |
| 未到达：重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格 | FAIL | `(reached)` | `(not reached) 重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格` |
| 未到达：重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格 | FAIL | `(reached)` | `(not reached) 重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格` |
| 未到达：重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格 | FAIL | `(reached)` | `(not reached) 重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
