# L2 场景证据：g3-exception-resume

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T080815909Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T080815909Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：结果 → DurableAck → 重启后新会话的恢复状态报告 → 同一 messageId 的结果按新会话代号补发 → 补发的 DurableAck（CV-OPERATION-RESULT-UNKNOWN-RECONCILE orderedExpectedMessages） | PASS | `Result(g2) < Ack < RecoveryStateReport(g3) < Result(g3, 同号) < Ack(新行哈希)` | `Result g2 ack=DurableAck / Report g3 / Replay g3 ack=DurableAck / 有序=True` |
| 车载端如实报未知并从日志补发：结果 overallOutcome 为 UNKNOWN；重启后的报告列出未结清尝试与这份结果；补发载荷与第一份逐字段相同（REPORT_UNKNOWN_AS_UNKNOWN / REPLAY_RESULT_ON_RECONNECT） | PASS | `UNKNOWN / unsettled=939c1f5e-cbdc-a050-9f9d-660bba2aaa6a / pendingResults 含该结果 / 载荷相同` | `UNKNOWN / unsettled=939c1f5e-cbdc-a050-9f9d-660bba2aaa6a / pendingResults 1 条、含该结果 1 / 载荷相同=True` |
| 未知不当成功、不重复提交：补发之后这次操作仍只有一行 UNKNOWN 结果，装载仍 RecoveryRequired，旅程仍 Blocked（NEVER_TREAT_UNKNOWN_AS_SUCCESS / forbidden unknown-as-success、duplicate-business-commit） | PASS | `1 行 UNKNOWN / RecoveryRequired / Blocked` | `1 行 UNKNOWN / RecoveryRequired / Blocked` |
| 服务端按报告的日志对账且不提前就绪：新会话代次大于重启前，补发之后待结清列表已清空，会话仍 RecoveryRequired（RECONCILE_FROM_REPORTED_JOURNAL / forbidden ready-before-reconciliation） | PASS | `代次 >2 / [] / RecoveryRequired` | `代次 3 / [] / RecoveryRequired (PENDING_FACT_RECONCILIATION_REQUIRED)` |
| 补发不碰物理：重启与补发之后这次操作没有再开锁，装载仓仍关门、空、锁上、输出复位（forbidden duplicate-slot-unlock / NO_UNPROVEN_STATE） | PASS | `UNLOCKING 1 次不变 / CLOSED/EMPTY/1/0` | `UNLOCKING 1 → 1 / CLOSED/EMPTY/1/0 → CLOSED/EMPTY/1/0` |
| 消息顺序与向量一致：ExceptionRecoverySessionRequested → Opened → RecoveryActionSubmitted(RESUME_AFTER_REPAIR) → Accepted → SlotOperationResumeCommand → OperationResult（CV-EXCEPTION-RESUME orderedExpectedMessages） | PASS | `SessionRequested→Opened ≤ ActionSubmitted→Accepted(RESUME_AFTER_REPAIR) ≤ ResumeCommand < OperationResult` | `SessionRequested→ExceptionRecoverySessionOpened / Action→RecoveryActionAccepted RESUME_AFTER_REPAIR / 有序=True` |
| 恢复会话只为已验证的管理员打开：会话记下车上配置的管理员与 MAINTENANCE_ADMINISTRATOR 角色，范围是这条需求与装载仓（OPEN_RECOVERY_SESSION_FOR_VERIFIED_ADMINISTRATOR） | PASS | `管理员 L2-OPERATOR / MAINTENANCE_ADMINISTRATOR / 1ef47b6b-e964-4516-a06e-0ba5e8b2c5fe / 1` | `L2-OPERATOR / MAINTENANCE_ADMINISTRATOR / 1ef47b6b-e964-4516-a06e-0ba5e8b2c5fe / [1]` |
| 只恢复授权的范围：恢复命令指向原尝试、原仓位与已证实的检查点；恢复之后车载端只重开了这个仓，且只开一次（AUTHORIZE_RESUME_SCOPE / RESUME_ONLY_AUTHORIZED_SCOPE / forbidden expanded-active-unlock-set） | PASS | `attempt 939c1f5e-cbdc-a050-9f9d-660bba2aaa6a / 仓 1 / 已证实检查点 / 恢复后开锁 1 次、仓 1` | `attempt 939c1f5e-cbdc-a050-9f9d-660bba2aaa6a / 仓 1 / SAFE_FINISH_REACHED / 恢复后开锁 1 次、仓 1` |
| 车载端报恢复后的结果：COMPLETED，服务端以它替换那份 UNKNOWN（第一行被第二行替换，第二行存活）（REPORT_RESUMED_OUTCOME） | PASS | `COMPLETED / UNKNOWN 被替换 → COMPLETED 存活` | `COMPLETED / UNKNOWN(被替换) → COMPLETED` |
| 恢复收敛且只提交一次：恢复工作流 Reconciled，恢复会话 CLOSED，装载 Committed 且只有这一笔，旅程离开 Blocked 继续往下走（finalState NO_DUPLICATE_COMMIT） | PASS | `Reconciled / CLOSED / Committed ×1 / 旅程已离开 Blocked` | `Reconciled / CLOSED / Committed ×1 / AwaitingStationDeparture` |
| 终态物理状态已证实：装载仓关门、有货、锁上、开锁输出复位，与车载端报的 COMPLETED 一致（finalState NO_UNPROVEN_STATE） | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
