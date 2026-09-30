# L2 场景证据：slot-fault-declaration

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260930T013340608Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `045935a54fe70f43343a67fb3eb81ccb6fdf7a4c` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260930T013340608Z` |
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
| 审计含 REQ-0359 列的全部项（判定人、角色、时间、车辆、Demand、尝试、仓位、类别、说明、带观测时刻的读数、车载端结果） | PASS | `all present` | `{"DeclarationId":"76b9279d-0a67-471a-a631-edb46b08a7e6","RequestId":"d1c1d80a-df1e-493a-b5ea-f7172a943ecd","RequestContentHash":"1d1f29cd57a91b5d15161eacdfb472159ad1a5cba8c66cd684baabb007b385a4","AgvId":"AGV-L2-001","DemandId":"bb8bf57b-286c-482f-80ff-cfa60c909312","SlotOperationAttemptId":"3d49cadc-614b-c359-95c0-e82b8d17c1db","OperationType":"UNLOAD","SlotNo":1,"FaultCategory":"LOCK","Note":"锁舌卡死，门推不开","AdministratorId":"L2-MAINTENANCE-383","AdministratorRole":"MAINTENANCE_ADMINISTRATOR","DeclaredAt":"2026-09-30 01:35:53.9700676+00:00","ReadingsJson":"{\"observedAt\":\"2026-09-30T01:34:55.5854381+00:00\",\"safetyStateVersion\":1,\"lockState\":\"LOCKED\",\"physicalState\":\"EMPTY\",\"unlockOutputState\":\"RESET\",\"changedSinceObserved\":false}","CommandMessageId":"573de318-1ff1-ed55-997f-2e3042d4aa2c","State":"APPLIED","ResultMessageId":"a4c24859-6631-4a0b-9e1d-d5a681d99bbe","ResultOutcome":"APPLIED","ResultProblemJson":null,"ResultReceivedAt":"2026-09-30 01:35:54.0285524+00:00"}` |
| 操作已判 UNKNOWN 后再判回 409 SLOT_FAULT_OPERATION_ALREADY_UNKNOWN | PASS | `409 / SLOT_FAULT_OPERATION_ALREADY_UNKNOWN` | `409 / SLOT_FAULT_OPERATION_ALREADY_UNKNOWN` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
