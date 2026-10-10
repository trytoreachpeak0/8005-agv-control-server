# L2 场景证据：g3-forced-mechanical-recovery

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T053231184Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `517e1c7a792936dd35037f1a1620de9f89411f20` |
| onboardHmiCommit | `ecdb3a0be1d95ef51e7d40493808ba4659274f41` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260922T053231184Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：RecoveryActionSubmitted(FORCED_MECHANICAL_RECOVERY) → RecoveryActionAccepted → ForcedMechanicalRecoveryCommand → ForcedMechanicalRecoveryResult（CV-FORCED-MECHANICAL-RECOVERY orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `Action×1 RecoveryActionAccepted FORCED_MECHANICAL_RECOVERY / Command×1 / Result×1 / 有序=True` |
| 强制恢复按代数设栅栏：接受这次动作使本车强制恢复代数恰好加一，工作流、命令都签在新代数下（FENCE_FORCED_RECOVERY_BY_GENERATION） | PASS | `代数 0 → 1 / 工作流 1 / 命令 1` | `代数 0 → 1 / 工作流 1 / 命令 1` |
| 车载端报强制恢复结果：MECHANICALLY_ISOLATED，带命令的代数，电子空载与车辆就绪两项证明都没有声称，只报仓位集合（REPORT_FORCED_RECOVERY_OUTCOME / REFUSE_STALE_FORCED_RECOVERY_GENERATION 的正向一半：车载端采纳的是当前代数） | PASS | `MECHANICALLY_ISOLATED / 代数 1 / proof false,false / 仓 1 / 无 slotResults` | `MECHANICALLY_ISOLATED / 代数 1 / proof False,False / 仓 1 / slotResults=False` |
| 强制恢复只结算货物业务：工作流 Reconciled，需求 Cancelled，旅程 Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF，恢复会话 CLOSED，没有去关卡；车辆会话仍 RecoveryRequired，等硬件恢复记录（REQ-0242 / forbidden ready-before-reconciliation、unknown-as-success） | PASS | `Reconciled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED / RecoveryRequired / Cancelled / TO_GATE 0` | `Reconciled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED / RecoveryRequired (FORCED_RECOVERY_GENERATION_MISMATCH) / Cancelled / TO_GATE 0` |
| 车辆没有替人撬门做电子动作：按下之后没有开锁，仓位物理状态不变，RIoT 上只有取货那一张单（forbidden duplicate-slot-unlock、duplicate-riot-order / NO_UNPROVEN_STATE） | PASS | `开锁 0 / 1=CLOSED/EMPTY/1/0 / RIoT 单 1` | `开锁 0 / 1=CLOSED/EMPTY/1/0 / RIoT 单 1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
