# L2 场景证据：g3-forced-mechanical-recovery

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T081031264Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d481250c08d65b9b03ba854f22f563fb33bfa5aa` |
| onboardHmiCommit | `c9d24dfd6e96dc43781735cac209dc3976fe3a83` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `4.0.0` |
| protocolReleaseIdentity.tag | `protocol-v4.0.0` |
| protocolReleaseIdentity.commit | `3fb214e57d861a909d872511940d26133eb32dca` |
| protocolReleaseIdentity.protocolVersion | `5` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a` |
| protocolReleaseIdentity.schemaBundleSha256 | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T081031264Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：RecoveryActionSubmitted(FORCED_MECHANICAL_RECOVERY) → RecoveryActionAccepted → ForcedMechanicalRecoveryCommand → ForcedMechanicalRecoveryResult（CV-FORCED-MECHANICAL-RECOVERY orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `Action×1 RecoveryActionAccepted FORCED_MECHANICAL_RECOVERY / Command×1 / Result×1 / 有序=True` |
| 强制恢复按代数设栅栏：接受这次动作使本车强制恢复代数恰好加一，工作流、命令都签在新代数下（FENCE_FORCED_RECOVERY_BY_GENERATION） | PASS | `代数 0 → 1 / 工作流 1 / 命令 1` | `代数 0 → 1 / 工作流 1 / 命令 1` |
| 车载端报强制恢复结果：MECHANICALLY_ISOLATED，带命令的代数，电子空载与车辆就绪两项证明都没有声称，只报仓位集合，抄回命令的需求并带具名交接记录（批号与交接人即界面上所填、批号即该需求的批号、带交接时刻）（REPORT_FORCED_RECOVERY_OUTCOME / REPORT_CARGO_HANDOFF_RECORD_IN_RESULT / COPY_COMMAND_DEMAND_INTO_RESULT / REFUSE_STALE_FORCED_RECOVERY_GENERATION 的正向一半：车载端采纳的是当前代数） | PASS | `MECHANICALLY_ISOLATED / 代数 1 / proof false,false / 仓 1 / 无 slotResults / 需求 9b91941c-de57-4592-9eb5-451b5908bec6 / 交接 G3-07M-20261016T081031264Z→G3 交接人 王五` | `MECHANICALLY_ISOLATED / 代数 1 / proof False,False / 仓 1 / slotResults=False / 需求 9b91941c-de57-4592-9eb5-451b5908bec6 / 交接 G3-07M-20261016T081031264Z→G3 交接人 王五 @ 10/16/2026 16:11:10` |
| 强制恢复只结算货物业务：工作流 Reconciled 并记下结果里的交接人，需求 Cancelled，旅程 Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF，恢复会话按交接 CLOSED（closedReason 为空），没有去关卡；车辆会话仍 RecoveryRequired，原因 FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED（等硬件恢复记录，不经重连；车载端报过的代数由结果更新为当前代数，cs#556）（REQ-0242 / SETTLE_DEMAND_ONLY_ON_NAMED_HANDOFF / forbidden ready-before-reconciliation、unknown-as-success） | PASS | `Reconciled（交接人 G3 交接人 王五）/ Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED（原因 NULL）/ RecoveryRequired (FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED，报过的代数 1) / Cancelled / TO_GATE 0` | `Reconciled（交接人 G3 交接人 王五）/ Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED（原因 NULL）/ RecoveryRequired (FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED，报过的代数 1) / Cancelled / TO_GATE 0` |
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
