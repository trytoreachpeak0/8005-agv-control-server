# L2 场景证据：g3-forced-mechanical-recovery

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T095310762Z` |
| agvId | `AGV-L2-001` |
| batchId | `cs556` |
| controlServerCommit | `4c0f5fa683b058436a58c6381ab0adc014593844` |
| onboardHmiCommit | `535c94fce47a10879ba1f404d04f603ba1a65bbf` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T095310762Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：RecoveryActionSubmitted(FORCED_MECHANICAL_RECOVERY) → RecoveryActionAccepted → ForcedMechanicalRecoveryCommand → ForcedMechanicalRecoveryResult（CV-FORCED-MECHANICAL-RECOVERY orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `Action×1 RecoveryActionAccepted FORCED_MECHANICAL_RECOVERY / Command×1 / Result×1 / 有序=True` |
| 强制恢复按代数设栅栏：接受这次动作使本车强制恢复代数恰好加一，工作流、命令都签在新代数下（FENCE_FORCED_RECOVERY_BY_GENERATION） | PASS | `代数 0 → 1 / 工作流 1 / 命令 1` | `代数 0 → 1 / 工作流 1 / 命令 1` |
| 车载端报强制恢复结果：MECHANICALLY_ISOLATED，带命令的代数，电子空载与车辆就绪两项证明都没有声称，只报仓位集合，抄回命令的需求并带具名交接记录（批号与交接人即界面上所填、批号即该需求的批号、带交接时刻）（REPORT_FORCED_RECOVERY_OUTCOME / REPORT_CARGO_HANDOFF_RECORD_IN_RESULT / COPY_COMMAND_DEMAND_INTO_RESULT / REFUSE_STALE_FORCED_RECOVERY_GENERATION 的正向一半：车载端采纳的是当前代数） | PASS | `MECHANICALLY_ISOLATED / 代数 1 / proof false,false / 仓 1 / 无 slotResults / 需求 7caa7b04-67a1-41f6-9ba8-b785e4342dce / 交接 G3-07M-20261010T095310762Z→G3 交接人 王五` | `MECHANICALLY_ISOLATED / 代数 1 / proof False,False / 仓 1 / slotResults=False / 需求 7caa7b04-67a1-41f6-9ba8-b785e4342dce / 交接 G3-07M-20261010T095310762Z→G3 交接人 王五 @ 10/10/2026 17:54:10` |
| 强制恢复只结算货物业务：工作流 Reconciled 并记下结果里的交接人，需求 Cancelled，旅程 Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF，恢复会话按交接 CLOSED（closedReason 为空），没有去关卡；车辆会话仍 RecoveryRequired，原因 FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED（等硬件恢复记录，不经重连；车载端报过的代数由结果更新为当前代数，cs#556）（REQ-0242 / SETTLE_DEMAND_ONLY_ON_NAMED_HANDOFF / forbidden ready-before-reconciliation、unknown-as-success） | FAIL | `Reconciled（交接人 G3 交接人 王五）/ Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED（原因 NULL）/ RecoveryRequired (FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED，报过的代数 1) / Cancelled / TO_GATE 0` | `Reconciled（交接人 G3 交接人 王五）/ Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED（原因 NULL）/ RecoveryRequired (FORCED_RECOVERY_GENERATION_MISMATCH，报过的代数 0) / Cancelled / TO_GATE 0` |
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
