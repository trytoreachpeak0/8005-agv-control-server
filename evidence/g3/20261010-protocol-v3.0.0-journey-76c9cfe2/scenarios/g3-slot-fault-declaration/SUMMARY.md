# L2 场景证据：g3-slot-fault-declaration

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T063933119Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `76c9cfe26f8bd06fc42f954b608edfdd108d5962` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
| protocolFaultProxy | `True` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T063933119Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端只在超时仓上受理判定：门槛之前判回 409 SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE、什么都不写；越过门槛后同一仓受理 | PASS | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` |
| 判定未结时装货照常结算：装货结果 COMPLETED、仓位操作 Committed，此刻判定仍 PENDING（SETTLE_OPERATION_RESULT_NORMALLY_WHILE_DECLARATION_PENDING） | PASS | `Committed / PENDING / COMPLETED` | `Committed / PENDING / COMPLETED` |
| 线上顺序与向量一致：补发的 SlotFaultDeclarationCommand（同一 messageId、新一代会话）→ SlotFaultDeclarationResult → DurableAck | PASS | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / 新一代` | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / gen 2 vs 1` |
| 车载端对已结的尝试回 NOT_APPLICABLE，problem.reasonCode = ACTION_NOT_ALLOWED_IN_STATE（REJECT_DECLARATION_ON_SETTLED_UNKNOWN_OR_SUPERSEDED_ATTEMPT） | PASS | `NOT_APPLICABLE / 44ecbfd5-07fa-595d-8709-3b8bc91895f1 / ACTION_NOT_ALLOWED_IN_STATE` | `{"declarationId":"1d87b07c-563d-4d63-9bb5-82b1485baf5a","slotOperationAttemptId":"44ecbfd5-07fa-595d-8709-3b8bc91895f1","outcome":"NOT_APPLICABLE","problem":{"reasonCode":"ACTION_NOT_ALLOWED_IN_STATE","fieldPath":"payload.slotOperationAttemptId","displayMessage":"本次仓位操作已结算，结果已上报。"}}` |
| 服务端撤销判定、不改任何业务状态：判定 NOT_APPLICABLE 并记下原因，装货仍 Committed、需求仍 Accepted、旅程没有阻断 | PASS | `NOT_APPLICABLE / Committed / Accepted / not Blocked` | `NOT_APPLICABLE / Committed / Accepted / AwaitingStationDeparture` |
| 线上顺序与向量一致：SlotFaultDeclarationCommand → SlotFaultDeclarationResult(APPLIED) → DurableAck → OperationResult → DurableAck（SEND_DECLARATION_RESULT_BEFORE_OPERATION_RESULT） | PASS | `202 / SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck / APPLIED` | `202 / SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck / APPLIED` |
| 被判仓 UNKNOWN 带 SLOT_FAULT_DECLARED、其余 NOT_STARTED、整体 UNKNOWN（REPORT_DECLARED_SLOT_UNKNOWN_LATER_SLOTS_NOT_STARTED） | PASS | `UNKNOWN / slot 1 UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED` | `{"demandId":"dae84bf4-3d4d-4cc0-84b3-087a2139fecd","slotOperationAttemptId":"9fb9f4a6-984e-2351-a7cf-6575e106f41d","operationType":"UNLOAD","overallOutcome":"UNKNOWN","slotResults":[{"slotNo":1,"outcome":"UNKNOWN","finalPhysicalState":"OCCUPIED","lockState":"UNLOCKED","unlockOutputState":"RESET","reasonCodes":["SLOT_FAULT_DECLARED"]}],"observedAt":"2026-10-10T14:42:49.509257+08:00","journalCheckpoint":"ACTIVE_UNLOCK_SET","resultContentSha256":"45cbe7e587107266c100a4190d2b358131e21ac3d18c41be9b46e9346102109c"}` |
| 终态与向量一致：卸货仓位操作 RecoveryRequired、旅程 Blocked（UNLOAD_RESULT_REQUIRES_RECOVERY）、需求未结算、会话 RECOVERY_REQUIRED（BLOCK_JOURNEY_ON_DECLARED_UNKNOWN） | PASS | `RecoveryRequired / Blocked UNLOAD_RESULT_REQUIRES_RECOVERY / not Succeeded / RecoveryRequired` | `RecoveryRequired / Blocked UNLOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / gen 2 / RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` |
| 审计判定与车载端结果（AUDIT_DECLARATION_AND_VEHICLE_RESULT）：判定人、角色、车、需求、尝试、仓、类别、说明、带观测时刻的读数、APPLIED 与收到时刻 | PASS | `all present` | `{"DeclarationId":"0b6e02da-dcec-4ea9-a150-d876e78af520","RequestId":"61e6f021-e0a7-4aa9-a1c0-5acf6eb522f4","RequestContentHash":"599574373ed6604921d1c83073274fd621ff53655a8d15b3b9f30d5d96d596ff","AgvId":"AGV-L2-001","DemandId":"dae84bf4-3d4d-4cc0-84b3-087a2139fecd","SlotOperationAttemptId":"9fb9f4a6-984e-2351-a7cf-6575e106f41d","OperationType":"UNLOAD","SlotNo":1,"FaultCategory":"LOCK","Note":"锁舌卡死，门推不开","AdministratorId":"G3-MAINTENANCE-383","AdministratorRole":"MAINTENANCE_ADMINISTRATOR","DeclaredAt":"2026-10-10 06:42:49.4036385+00:00","ReadingsJson":"{\"observedAt\":\"2026-10-10T06:42:49.156355+00:00\",\"safetyStateVersion\":15,\"lockState\":\"UNLOCKED\",\"physicalState\":\"OCCUPIED\",\"unlockOutputState\":\"RESET\",\"changedSinceObserved\":false}","CommandMessageId":"45f2672a-5039-9b5f-a174-598f08bbf441","State":"APPLIED","ResultMessageId":"61bf5cdc-612b-6058-9473-7d3b8e1405f0","ResultOutcome":"APPLIED","ResultProblemJson":null,"ResultReceivedAt":"2026-10-10 06:42:49.4510219+00:00"}` |
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
