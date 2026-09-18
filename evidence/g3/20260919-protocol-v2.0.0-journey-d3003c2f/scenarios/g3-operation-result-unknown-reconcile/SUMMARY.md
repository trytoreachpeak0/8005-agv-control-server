# L2 场景证据：g3-operation-result-unknown-reconcile

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T183626103Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `d3003c2f688431436850df634a7d028fcb14f346` |
| onboardHmiCommit | `29fbf65e0b4d58c80849d5e6d0e44f40903c411e` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260918T183626103Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：结果 → 它的 DurableAck → 重启后新会话的恢复状态报告 → 同一 messageId 的结果按新会话代号补发 → 补发的 DurableAck（CV-OPERATION-RESULT-UNKNOWN-RECONCILE orderedExpectedMessages） | PASS | `Result(g2) < Ack < RecoveryStateReport(g3) < Result(g3, 同号) < Ack(新行哈希)` | `Result g2 ack=DurableAck / Report g3 / Replay g3 ack=DurableAck / 有序=True` |
| 车载端如实报未知并从日志补发：结果 overallOutcome 为 UNKNOWN；重启后的报告把这次操作列为未结清、把这份结果列进 pendingResults；补发的载荷与第一份逐字段相同（REPORT_UNKNOWN_AS_UNKNOWN / REPLAY_RESULT_ON_RECONNECT） | PASS | `UNKNOWN / unsettled=4f9d7c91-96b5-f556-aa89-e84a2a5e9b10 / pendingResults 含该结果 / 载荷相同` | `UNKNOWN / unsettled=4f9d7c91-96b5-f556-aa89-e84a2a5e9b10 / pendingResults 1 条、含该结果 1 / 载荷相同=True` |
| 未知不当成功、不重复提交：这次操作只有一行结果且为 UNKNOWN，装载仍 RecoveryRequired，需求未成功，旅程仍停在 Blocked，没有去关卡的意图也没有关卡单（NEVER_TREAT_UNKNOWN_AS_SUCCESS / forbidden unknown-as-success、duplicate-business-commit） | PASS | `1 行 UNKNOWN / RecoveryRequired / 未成功 / Blocked / 意图 0 / 关卡单 0` | `1 行 UNKNOWN / RecoveryRequired / RecoveryRequired / Blocked / 意图 0 / 关卡单 0` |
| 服务端按报告的日志对账且不提前就绪：新会话代次大于重启前，补发之后待结清列表已清空，会话仍 RecoveryRequired（RECONCILE_FROM_REPORTED_JOURNAL / forbidden ready-before-reconciliation） | PASS | `代次 >2 / [] / RecoveryRequired` | `代次 3 / [] / RecoveryRequired (PENDING_FACT_RECONCILIATION_REQUIRED)` |
| 补发不碰物理：重启之后这次操作没有再开锁，装载仓仍是关门、空、锁上、开锁输出复位（forbidden duplicate-slot-unlock / finalState NO_UNPROVEN_STATE） | PASS | `UNLOCKING 1 次不变 / CLOSED/EMPTY/1/0` | `UNLOCKING 1 → 1 / CLOSED/EMPTY/1/0 → CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
