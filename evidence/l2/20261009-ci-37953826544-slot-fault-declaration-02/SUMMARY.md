# L2 场景证据：slot-fault-declaration

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T164501911Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T164501911Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装货站的判定被服务端受理（202，PENDING） | PASS | `202 / PENDING` | `202 / PENDING` |
| 车载端拒绝判定，服务端记下 NOT_APPLICABLE 与原因 | PASS | `NOT_APPLICABLE / ACTION_NOT_ALLOWED_IN_STATE` | `NOT_APPLICABLE / {"reasonCode":"ACTION_NOT_ALLOWED_IN_STATE","fieldPath":"payload.slotOperationAttemptId","displayMessage":"The declared slot is no longer waiting for the operator."}` |
| 被拒的判定不改任何业务状态：旅程仍等装货结果、仓位操作仍 Prepared、需求仍 Accepted | PASS | `AwaitingLoadResult / Prepared / Accepted` | `AwaitingLoadResult / Prepared / Accepted` |
| 装货照常提交（Committed），车继续去卸货站 | PASS | `Committed` | `Committed` |
| 卸货站的判定被受理并已下发到车（202，PENDING，sentToVehicle） | PASS | `202 / PENDING / True` | `202 / PENDING / True` |
| 旅程转 Blocked，原因 UNLOAD_RESULT_REQUIRES_RECOVERY | PASS | `Blocked / UNLOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / UNLOAD_RESULT_REQUIRES_RECOVERY` |
| 判定记录为 APPLIED | PASS | `APPLIED` | `APPLIED` |
| 卸货仓位操作转 RecoveryRequired（走既有 UNKNOWN 结算） | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 审计含 REQ-0359 列的全部项（判定人、角色、时间、车辆、Demand、尝试、仓位、类别、说明、带观测时刻的读数、车载端结果） | PASS | `all present` | `{"DeclarationId":"2c2da957-0e86-42d6-8c4a-5c6b408adc12","RequestId":"8c6bc597-6eca-484a-a748-060d6b649649","RequestContentHash":"1d1f29cd57a91b5d15161eacdfb472159ad1a5cba8c66cd684baabb007b385a4","AgvId":"AGV-L2-001","DemandId":"e23e3372-7387-464f-adf0-00478a97cce4","SlotOperationAttemptId":"71c4bd45-7d46-3e50-9678-feb89a164d0a","OperationType":"UNLOAD","SlotNo":1,"FaultCategory":"LOCK","Note":"锁舌卡死，门推不开","AdministratorId":"L2-MAINTENANCE-383","AdministratorRole":"MAINTENANCE_ADMINISTRATOR","DeclaredAt":"2026-10-09 16:46:02.2988051+00:00","ReadingsJson":"{\"observedAt\":\"2026-10-09T16:45:12.4306152+00:00\",\"safetyStateVersion\":1,\"lockState\":\"LOCKED\",\"physicalState\":\"EMPTY\",\"unlockOutputState\":\"RESET\",\"changedSinceObserved\":false}","CommandMessageId":"03e21d31-b41d-4655-a7f5-fd595a377ae1","State":"APPLIED","ResultMessageId":"e1361ae5-f1a7-46e8-bf0e-b2695e287a8b","ResultOutcome":"APPLIED","ResultProblemJson":null,"ResultReceivedAt":"2026-10-09 16:46:02.3223215+00:00"}` |
| 操作已判 UNKNOWN 后再判回 409 SLOT_FAULT_OPERATION_ALREADY_UNKNOWN | PASS | `409 / SLOT_FAULT_OPERATION_ALREADY_UNKNOWN` | `409 / SLOT_FAULT_OPERATION_ALREADY_UNKNOWN` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
