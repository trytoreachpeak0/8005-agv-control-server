# L2 场景证据：g3-slot-fault-declaration

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T081152618Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d481250c08d65b9b03ba854f22f563fb33bfa5aa` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `c9d24dfd6e96dc43781735cac209dc3976fe3a83` |
| protocolFaultProxy | `True` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T081152618Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端只在超时仓上受理判定：门槛之前判回 409 SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE、什么都不写；越过门槛后同一仓受理 | PASS | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` |
| 判定未结时装货照常结算：装货结果 COMPLETED、仓位操作 Committed，此刻判定仍 PENDING（SETTLE_OPERATION_RESULT_NORMALLY_WHILE_DECLARATION_PENDING） | PASS | `Committed / PENDING / COMPLETED` | `Committed / PENDING / COMPLETED` |
| 线上顺序与向量一致：补发的 SlotFaultDeclarationCommand（同一 messageId、新一代会话）→ SlotFaultDeclarationResult → DurableAck | PASS | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / 新一代` | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / gen 2 vs 1` |
| 车载端对已结的尝试回 NOT_APPLICABLE，problem.reasonCode = ACTION_NOT_ALLOWED_IN_STATE（REJECT_DECLARATION_ON_SETTLED_UNKNOWN_OR_SUPERSEDED_ATTEMPT） | PASS | `NOT_APPLICABLE / a1f7fe92-04d1-935d-aa0a-cd3f48285cf1 / ACTION_NOT_ALLOWED_IN_STATE` | `{"declarationId":"94cf63fd-6f24-4dad-a493-094c93fa0219","slotOperationAttemptId":"a1f7fe92-04d1-935d-aa0a-cd3f48285cf1","outcome":"NOT_APPLICABLE","problem":{"reasonCode":"ACTION_NOT_ALLOWED_IN_STATE","fieldPath":"payload.slotOperationAttemptId","displayMessage":"本次仓位操作已结算，结果已上报。"}}` |
| 服务端撤销判定、不改任何业务状态：判定 NOT_APPLICABLE 并记下原因，装货仍 Committed、需求仍 Accepted、旅程没有阻断 | PASS | `NOT_APPLICABLE / Committed / Accepted / not Blocked` | `NOT_APPLICABLE / Committed / Accepted / AwaitingStationDeparture` |
| 线上顺序与向量一致：SlotFaultDeclarationCommand → SlotFaultDeclarationResult(APPLIED) → DurableAck → OperationResult → DurableAck（SEND_DECLARATION_RESULT_BEFORE_OPERATION_RESULT） | PASS | `202 / SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck / APPLIED` | `202 / SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck / APPLIED` |
| 被判仓 UNKNOWN 带 SLOT_FAULT_DECLARED、其余 NOT_STARTED、整体 UNKNOWN（REPORT_DECLARED_SLOT_UNKNOWN_LATER_SLOTS_NOT_STARTED） | PASS | `UNKNOWN / slot 1 UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED` | `{"demandId":"fadf3743-c3e3-46a4-8325-80f658ecc98a","slotOperationAttemptId":"6ff461f7-ad22-2a55-af8f-834afda1eddf","operationType":"UNLOAD","overallOutcome":"UNKNOWN","slotResults":[{"slotNo":1,"outcome":"UNKNOWN","finalPhysicalState":"OCCUPIED","lockState":"UNLOCKED","unlockOutputState":"RESET","reasonCodes":["SLOT_FAULT_DECLARED"]}],"observedAt":"2026-10-16T16:15:08.6606432+08:00","journalCheckpoint":"ACTIVE_UNLOCK_SET","resultContentSha256":"b57349fc4cd0c169021e853e1895f090b185d2da047fb4abccffc93a2f06c4f6"}` |
| 终态与向量一致：卸货仓位操作 RecoveryRequired、旅程 Blocked（UNLOAD_RESULT_REQUIRES_RECOVERY）、需求未结算、会话 RECOVERY_REQUIRED（BLOCK_JOURNEY_ON_DECLARED_UNKNOWN） | PASS | `RecoveryRequired / Blocked UNLOAD_RESULT_REQUIRES_RECOVERY / not Succeeded / RecoveryRequired` | `RecoveryRequired / Blocked UNLOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / gen 2 / RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` |
| 审计判定与车载端结果（AUDIT_DECLARATION_AND_VEHICLE_RESULT）：判定人、角色、车、需求、尝试、仓、类别、说明、带观测时刻的读数、APPLIED 与收到时刻 | PASS | `all present` | `{"DeclarationId":"7742eeea-4076-43f7-a3f1-d9780c15a4e3","RequestId":"d3efdfcf-50b8-40e9-b5b2-b4083bf56cfa","RequestContentHash":"599574373ed6604921d1c83073274fd621ff53655a8d15b3b9f30d5d96d596ff","AgvId":"AGV-L2-001","DemandId":"fadf3743-c3e3-46a4-8325-80f658ecc98a","SlotOperationAttemptId":"6ff461f7-ad22-2a55-af8f-834afda1eddf","OperationType":"UNLOAD","SlotNo":1,"FaultCategory":"LOCK","Note":"锁舌卡死，门推不开","AdministratorId":"G3-MAINTENANCE-383","AdministratorRole":"MAINTENANCE_ADMINISTRATOR","DeclaredAt":"2026-10-16 08:15:08.5697769+00:00","ReadingsJson":"{\"observedAt\":\"2026-10-16T08:15:08.4943503+00:00\",\"safetyStateVersion\":13,\"lockState\":\"UNLOCKED\",\"physicalState\":\"OCCUPIED\",\"unlockOutputState\":\"RESET\",\"changedSinceObserved\":false}","CommandMessageId":"2eb0154d-609f-305c-95d9-cbd782ddc351","State":"APPLIED","ResultMessageId":"4ccdcba6-065e-7c57-83c9-2a599a85ab7e","ResultOutcome":"APPLIED","ResultProblemJson":null,"ResultReceivedAt":"2026-10-16 08:15:08.6056986+00:00"}` |
| 判定生效后零开锁（NEVER_UNLOCK_AFTER_DECLARATION_APPLIED）：空关后 10 秒门仍关着，卸货的 UNLOCKING 进度条数不变 | PASS | `CLOSED/* / UNLOCKING 1` | `CLOSED/OCCUPIED/1/0 / UNLOCKING 1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
