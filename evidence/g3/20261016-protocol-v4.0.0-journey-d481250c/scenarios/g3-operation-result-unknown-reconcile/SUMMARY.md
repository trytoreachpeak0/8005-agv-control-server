# L2 场景证据：g3-operation-result-unknown-reconcile

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T080735067Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T080735067Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：结果 → 它的 DurableAck → 重启后新会话的恢复状态报告 → 同一 messageId 的结果按新会话代号补发 → 补发的 DurableAck（CV-OPERATION-RESULT-UNKNOWN-RECONCILE orderedExpectedMessages） | PASS | `Result(g2) < Ack < RecoveryStateReport(g3) < Result(g3, 同号) < Ack(新行哈希)` | `Result g2 ack=DurableAck / Report g3 / Replay g3 ack=DurableAck / 有序=True` |
| 车载端如实报未知并从日志补发：结果 overallOutcome 为 UNKNOWN；重启后的报告把这次操作列为未结清、把这份结果列进 pendingResults；补发的载荷与第一份逐字段相同（REPORT_UNKNOWN_AS_UNKNOWN / REPLAY_RESULT_ON_RECONNECT） | PASS | `UNKNOWN / unsettled=fad735ee-dc0a-ad53-a031-e89b4d691aeb / pendingResults 含该结果 / 载荷相同` | `UNKNOWN / unsettled=fad735ee-dc0a-ad53-a031-e89b4d691aeb / pendingResults 1 条、含该结果 1 / 载荷相同=True` |
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
