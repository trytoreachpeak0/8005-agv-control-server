# L2 场景证据：real-onboard-restart-with-open-recovery-session

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T150437727Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `84a749e335a3c38cf0d317cf44cccf5762b434ca` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35450443032-1\_stage\l2-20260919T150437727Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked/LOAD_RESULT_REQUIRES_RECOVERY` |
| 未到达：装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话 | FAIL | `(reached)` | `(not reached) 装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话` |
| 未到达：装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话 | FAIL | `(reached)` | `(not reached) 装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话` |
| 未到达：装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话 | FAIL | `(reached)` | `(not reached) 装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话` |
| 未到达：装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话 | FAIL | `(reached)` | `(not reached) 装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话` |
| 未到达：装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话 | FAIL | `(reached)` | `(not reached) 装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话` |
| 未到达：装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话 | FAIL | `(reached)` | `(not reached) 装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话` |
| 未到达：装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话 | FAIL | `(reached)` | `(not reached) 装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
