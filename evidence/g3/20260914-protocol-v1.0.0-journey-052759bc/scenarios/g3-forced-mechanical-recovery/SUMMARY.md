# L2 场景证据：g3-forced-mechanical-recovery

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T043019601Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `052759bca58a04316dfda260249b3b5697f2cf8e` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T043019601Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：RecoveryActionSubmitted(FORCED_MECHANICAL_RECOVERY) → RecoveryActionAccepted → ForcedMechanicalRecoveryCommand → ForcedMechanicalRecoveryResult（CV-FORCED-MECHANICAL-RECOVERY orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `Action×1 RecoveryActionAccepted FORCED_MECHANICAL_RECOVERY / Command×1 / Result×1 / 有序=True` |
| 强制恢复按代数设栅栏：接受这次动作使本车强制恢复代数恰好加一，工作流、命令都签在新代数下（FENCE_FORCED_RECOVERY_BY_GENERATION） | PASS | `代数 0 → 1 / 工作流 1 / 命令 1` | `代数 0 → 1 / 工作流 1 / 命令 1` |
| 车载端报强制恢复结果：MECHANICALLY_ISOLATED，带命令的代数，电子空载与车辆就绪两项证明都没有声称，只报仓位集合（REPORT_FORCED_RECOVERY_OUTCOME / REFUSE_STALE_FORCED_RECOVERY_GENERATION 的正向一半：车载端采纳的是当前代数） | PASS | `MECHANICALLY_ISOLATED / 代数 1 / proof false,false / 仓 1 / 无 slotResults` | `MECHANICALLY_ISOLATED / 代数 1 / proof False,False / 仓 1 / slotResults=False` |
| 强制恢复不当作已对账：工作流仍 RecoveryRequired，旅程停在 Blocked 且原因要求重新核对，会话 RecoveryRequired，需求未成功，没有去关卡（finalState readiness RECOVERY_REQUIRED_OR_UNIQUELY_RECONCILED / forbidden ready-before-reconciliation、unknown-as-success） | PASS | `RecoveryRequired / Blocked/FORCED_MECHANICAL_RECOVERY_REQUIRES_FRESH_RECONCILIATION / RecoveryRequired / 未成功 / TO_GATE 0` | `RecoveryRequired / Blocked/FORCED_MECHANICAL_RECOVERY_REQUIRES_FRESH_RECONCILIATION / RecoveryRequired (FORCED_RECOVERY_GENERATION_MISMATCH) / RecoveryRequired / TO_GATE 0` |
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
