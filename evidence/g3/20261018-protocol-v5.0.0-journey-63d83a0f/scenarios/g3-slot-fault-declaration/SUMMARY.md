# L2 场景证据：g3-slot-fault-declaration

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261018T074629263Z` |
| agvId | `AGV-L2-001` |
| batchId | `v5-exit` |
| controlServerCommit | `63d83a0f623a99d0260c2b1c95672a91584336b5` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `a3a6753f6924b8f845483d6b886259c90af9b018` |
| protocolFaultProxy | `True` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261018T074629263Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端只在超时仓上受理判定：门槛之前判回 409 SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE、什么都不写；越过门槛后同一仓受理 | PASS | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` |
| 判定未结时装货照常结算：装货结果 COMPLETED、仓位操作 Committed，此刻判定仍 PENDING（SETTLE_OPERATION_RESULT_NORMALLY_WHILE_DECLARATION_PENDING） | PASS | `Committed / PENDING / COMPLETED` | `Committed / PENDING / COMPLETED` |
| 线上顺序与向量一致：补发的 SlotFaultDeclarationCommand（同一 messageId、新一代会话）→ SlotFaultDeclarationResult → DurableAck | PASS | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / 新一代` | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / gen 2 vs 1` |
| 车载端对已结的尝试回 NOT_APPLICABLE，problem.reasonCode = ACTION_NOT_ALLOWED_IN_STATE（REJECT_DECLARATION_ON_SETTLED_UNKNOWN_OR_SUPERSEDED_ATTEMPT） | PASS | `NOT_APPLICABLE / 79bcbd1b-821e-fb5a-882c-1fdeb716474d / ACTION_NOT_ALLOWED_IN_STATE` | `{"declarationId":"6572cac9-db17-4f99-9f33-027ca7ebb26b","slotOperationAttemptId":"79bcbd1b-821e-fb5a-882c-1fdeb716474d","outcome":"NOT_APPLICABLE","problem":{"reasonCode":"ACTION_NOT_ALLOWED_IN_STATE","fieldPath":"payload.slotOperationAttemptId","displayMessage":"本次仓位操作已结算，结果已上报。"}}` |
| 服务端撤销判定、不改任何业务状态：判定 NOT_APPLICABLE 并记下原因，装货仍 Committed、需求仍 Accepted、旅程没有阻断 | PASS | `NOT_APPLICABLE / Committed / Accepted / not Blocked` | `NOT_APPLICABLE / Committed / Accepted / AwaitingStationDeparture` |
| 线上顺序与向量一致：SlotFaultDeclarationCommand → SlotFaultDeclarationResult(APPLIED) → DurableAck → OperationResult → DurableAck（SEND_DECLARATION_RESULT_BEFORE_OPERATION_RESULT） | PASS | `202 / SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck / APPLIED` | `202 / SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck / APPLIED` |
| 被判仓 UNKNOWN 带 SLOT_FAULT_DECLARED、其余 NOT_STARTED、整体 UNKNOWN（REPORT_DECLARED_SLOT_UNKNOWN_LATER_SLOTS_NOT_STARTED） | PASS | `UNKNOWN / slot 1 UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED` | `{"demandId":"8a3f4d57-6549-44ed-ab68-6a6bc01a8f75","slotOperationAttemptId":"93db2538-d01a-9557-a52b-44d4f504da10","operationType":"UNLOAD","overallOutcome":"UNKNOWN","slotResults":[{"slotNo":1,"outcome":"UNKNOWN","finalPhysicalState":"OCCUPIED","lockState":"UNLOCKED","unlockOutputState":"RESET","reasonCodes":["SLOT_FAULT_DECLARED"]}],"observedAt":"2026-10-18T15:50:03.1561555+08:00","journalCheckpoint":"ACTIVE_UNLOCK_SET","resultContentSha256":"419e758cb4faf3061021f845f12cc58c5fc98eec2d60995e7d19675d7c9c5c95"}` |
| 终态与向量一致：卸货仓位操作 RecoveryRequired、旅程 Blocked（UNLOAD_RESULT_REQUIRES_RECOVERY）、需求未结算、会话 RECOVERY_REQUIRED（BLOCK_JOURNEY_ON_DECLARED_UNKNOWN） | PASS | `RecoveryRequired / Blocked UNLOAD_RESULT_REQUIRES_RECOVERY / not Succeeded / RecoveryRequired` | `RecoveryRequired / Blocked UNLOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / gen 2 / RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` |
| 审计判定与车载端结果（AUDIT_DECLARATION_AND_VEHICLE_RESULT）：判定人、角色、车、需求、尝试、仓、类别、说明、带观测时刻的读数、APPLIED 与收到时刻 | PASS | `all present` | `{"DeclarationId":"5463617d-d777-4778-978d-d5416b45126d","RequestId":"13f3f4bc-1d63-4c6c-b18a-3a915f5b1e59","RequestContentHash":"599574373ed6604921d1c83073274fd621ff53655a8d15b3b9f30d5d96d596ff","AgvId":"AGV-L2-001","DemandId":"8a3f4d57-6549-44ed-ab68-6a6bc01a8f75","SlotOperationAttemptId":"93db2538-d01a-9557-a52b-44d4f504da10","OperationType":"UNLOAD","SlotNo":1,"FaultCategory":"LOCK","Note":"锁舌卡死，门推不开","AdministratorId":"G3-MAINTENANCE-383","AdministratorRole":"MAINTENANCE_ADMINISTRATOR","DeclaredAt":"2026-10-18 07:50:02.8815049+00:00","ReadingsJson":"{\"observedAt\":\"2026-10-18T07:50:02.7748339+00:00\",\"safetyStateVersion\":15,\"lockState\":\"UNLOCKED\",\"physicalState\":\"OCCUPIED\",\"unlockOutputState\":\"RESET\",\"changedSinceObserved\":false}","CommandMessageId":"639ce912-54c4-e457-b5a3-c87ff60132f7","State":"APPLIED","ResultMessageId":"62bb3dad-b967-c65c-afcd-05b0260ba298","ResultOutcome":"APPLIED","ResultProblemJson":null,"ResultReceivedAt":"2026-10-18 07:50:02.9842601+00:00"}` |
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
