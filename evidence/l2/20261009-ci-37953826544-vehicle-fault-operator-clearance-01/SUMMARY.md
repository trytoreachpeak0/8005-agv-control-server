# L2 场景证据：vehicle-fault-operator-clearance

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163052200Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163052200Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 急停锁着：清除入口回 409，理由点名 FAULT_RECOVERY_EMERGENCY_LATCHED；故障仍是 SuspectedBlocked，没有发 cancelEmergency（清除不解除急停） | PASS | `409 / FAULT_RECOVERY_EMERGENCY_LATCHED / SuspectedBlocked / 0 次` | `409 / FAULT_RECOVERY_EMERGENCY_LATCHED / SuspectedBlocked / 0 次` |
| 急停解除之后故障仍在（解除只结束急停）：SuspectedBlocked、未清除；车一直挂在原旅程上，没有新旅程 | PASS | `SuspectedBlocked / 1 趟旅程` | `SuspectedBlocked / 1 趟旅程` |
| 没确认故障已排除、RIoT 里这台车还有未结束的订单：入口回 409，三条理由（没确认、车上有未完成订单、当前单没结束）一起列出；故障不动 | PASS | `409 / REMEDY_NOT_CONFIRMED + VEHICLE_ORDER_NOT_FINISHED + CURRENT_ORDER_NOT_ENDED / SuspectedBlocked` | `409 / FAULT_RECOVERY_REMEDY_NOT_CONFIRMED,FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED,FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED / SuspectedBlocked` |
| 条件齐全：入口回 200，结果 Cleared、处置 REBUILD_SCHEDULED；故障 Level None，ClearedReason 记着操作员 L2-OPERATOR-07；旧旅程不关闭、停在开往取货站，码 VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD，需求不释放（翻转自 #299 的「释放改派、旧旅程关闭」，依据 issuecomment-5787511271） | PASS | `200 / Cleared / REBUILD_SCHEDULED / None / L2-OPERATOR-07 / 1 趟 AwaitingPickupArrival VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD / 需求未移除` | `200 / Cleared / REBUILD_SCHEDULED / None / FAULT_CLEARED_BY_OPERATOR:L2-OPERATOR-07 / 1 趟 AwaitingPickupArrival VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD / 需求未移除` |
| 延迟之后重建：同一趟旅程的取货停靠改指向一张服务端自己的新单（W2G-…），意图已确认，同一条需求、同一辆车；RIoT 上有这张单、指派给这辆车；没有新旅程 | PASS | `新单号 W2G-… / CONFIRMED / 需求 1afebdb8-5f02-41be-8771-9b88e4689b3a / BROKERX-L2-0001 / RIoT 1 张 / 1 趟旅程` | `W2G-1afebdb8-5f02-41be-8771-9b88e4689b3a-REBUILD-1-1 / CONFIRMED / 需求 1afebdb8-5f02-41be-8771-9b88e4689b3a / BROKERX-L2-0001 / RIoT 1 张 / 1 趟旅程` |
| 清除之后又评估了 4 轮：故障没有再记（Level None、代次仍是 1），没有新的急停（仍只 1 次 triggerEmergency） | PASS | `None / 代次 1 / 1 次` | `None / 代次 1 / 1 次` |
| 同一个清除请求再来一次：200、AlreadyCleared，旅程表一行不变（不碰清除之后重建出来接着走的这趟旅程） | PASS | `200 / AlreadyCleared / 旅程不变` | `200 / AlreadyCleared / 旅程不变` |
| 已经清过的车，再来一个没署名的请求：409，理由只有 FAULT_RECOVERY_OPERATOR_UNIDENTIFIED，不答「已经清过」，旅程表一行不变 | PASS | `409 / FAULT_RECOVERY_OPERATOR_UNIDENTIFIED / 旅程不变` | `409 / FAULT_RECOVERY_OPERATOR_UNIDENTIFIED / 旅程不变` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
