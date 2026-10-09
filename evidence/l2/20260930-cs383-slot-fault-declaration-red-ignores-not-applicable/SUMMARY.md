# L2 场景证据：slot-fault-declaration

结论：**FAIL**

失败原因：Timed out after 120s waiting for: the load completed and the journey reached the gate leg. Last observed: "Blocked"

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260930T013640473Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260930T013640473Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装货站的判定被服务端受理（202，PENDING） | PASS | `202 / PENDING` | `202 / PENDING` |
| 车载端拒绝判定，服务端记下 NOT_APPLICABLE 与原因 | PASS | `NOT_APPLICABLE / ACTION_NOT_ALLOWED_IN_STATE` | `NOT_APPLICABLE / {"reasonCode":"ACTION_NOT_ALLOWED_IN_STATE","fieldPath":"payload.slotOperationAttemptId","displayMessage":"The declared slot is no longer waiting for the operator."}` |
| 被拒的判定不改任何业务状态：旅程仍等装货结果、仓位操作仍 Prepared、需求仍 Accepted | FAIL | `AwaitingLoadResult / Prepared / Accepted` | `Blocked / RecoveryRequired / RecoveryRequired` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
