# L2 场景证据：emergency-stop-operator-release

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160700218Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160700218Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 锁住即停稳（REQ-0247）：车报 MT_RUNNING、车速 0、站点 0，故障事实仍记为已停稳，服务端没有再请求停车 | PASS | `StopProven 1 / 1 行 / 1 次` | `StopProven 1 / 1 行 / 1 次` |
| 没确认「车上无货」：入口回 409，原因里点名 EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED，没有发 cancelEmergency | PASS | `409 / EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED / 0 次` | `409 / EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED / 0 次` |
| 这台车在 RIoT 里还有未结束的订单：入口回 409，原因 EMERGENCY_VEHICLE_ORDER_NOT_FINISHED，没有发 cancelEmergency（REQ-0356） | PASS | `409 / EMERGENCY_VEHICLE_ORDER_NOT_FINISHED / 0 次` | `409 / EMERGENCY_VEHICLE_ORDER_NOT_FINISHED / 0 次` |
| 确认齐全：服务端对这台车发出一次 cancelEmergency；RIoT 还没解锁，入口回 202、动作 RecoveryUnconfirmed，不谎报成功 | PASS | `202 / RecoveryUnconfirmed / 1 次 @ BROKERX-L2-0001 / 1 行` | `202 / RecoveryUnconfirmed / 1 次 / 1 行` |
| 解除记下了确认人与三项确认：来源 ServerOperator、工号 L2-OPERATOR-07、causeCleared/vehicleEmpty/allDoorsClosed 均为 true | PASS | `ServerOperator / L2-OPERATOR-07 / 三项 true` | `{"source":"ServerOperator","requesterIdentity":"L2-OPERATOR-07","agvId":"AGV-L2-001","deviceKey":"BROKERX-L2-0001","Reason":"EMERGENCY_RELEASE_CONFIRMED_BY_OPERATOR","call":{"disposition":"Accepted","Operation":"cancelEmergency","Classification":"SdkAccepted","HttpStatusCode":null,"BusinessCode":null,"FailureCategory":null,"observedAt":"2026-10-09T16:07:30.1670202\u002B00:00"},"emergencyState":"CAN_RECOVER","releaseConfirmation":{"causeCleared":true,"vehicleEmpty":true,"allDoorsClosed":true,"note":"L2 20261009T160700218Z"}}` |
| RIoT 读到 OK 之后，那一次解除被改记为 Confirmed，急停就此结束 | PASS | `Confirmed` | `Confirmed` |
| 解除之后又评估了 6 轮：车停在两站之间、位置读不到，服务端既没有按意外恢复重触发，也没有再急停（仍只 1 次 triggerEmergency、1 次 cancelEmergency） | PASS | `1 行 / 1 次 / 1 次` | `1 行 / 1 次 / 1 次` |
| 解除只结束急停：车辆故障事实仍是 SuspectedBlocked、未清除，这台车照样不派新单 | PASS | `SuspectedBlocked / 未清除` | `SuspectedBlocked / ClearedAt=` |
| 急停已经解开后再点一次确认：入口回 409（EMERGENCY_NOT_CAN_RECOVER），不再发任何调用 | PASS | `409 / EMERGENCY_NOT_CAN_RECOVER / 仍 1 次` | `409 / EMERGENCY_NOT_CAN_RECOVER,EMERGENCY_NOT_RAISED_BY_8005 / 1 次` |
| 解除之后读到车在动：服务端发出新的一次 triggerEmergency，原因不是 EMERGENCY_LATCH_RELEASED_EXTERNALLY（不是意外恢复，是新的急停） | PASS | `AttemptNumber 2 / 非意外恢复 / 2 次` | `AttemptNumber 2 / {"source":"Automatic","requesterIdentity":null,"agvId":"AGV-L2-001","deviceKey":"BROKERX-L2-0001","Reason":"STOP_PROOF_MOTION_OBSERVED,STOP_PROOF_POSITION_UNKNOWN","call":{"disposition":"Accepted","Operation":"triggerEmergency","Classification":"SdkAccepted","HttpStatusCode":null,"BusinessCode":null,"FailureCategory":null,"observedAt":"2026-10-09T16:07:37.9556994\u002B00:00"},"emergencyState":"OK"} / 2 次` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
