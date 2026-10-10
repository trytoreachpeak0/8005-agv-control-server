# L2 场景证据：vehicle-fault-operator-clearance

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260923T032950621Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `af01fd2716a814f38bdee1e563a4da8619a14c4f` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260923T032950621Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 急停锁着：清除入口回 409，理由点名 FAULT_RECOVERY_EMERGENCY_LATCHED；故障仍是 SuspectedBlocked，没有发 cancelEmergency（清除不解除急停） | PASS | `409 / FAULT_RECOVERY_EMERGENCY_LATCHED / SuspectedBlocked / 0 次` | `409 / FAULT_RECOVERY_EMERGENCY_LATCHED / SuspectedBlocked / 0 次` |
| 急停解除之后故障仍在（解除只结束急停）：SuspectedBlocked、未清除；车一直挂在原旅程上，没有新旅程 | PASS | `SuspectedBlocked / 1 趟旅程` | `SuspectedBlocked / 1 趟旅程` |
| 没确认故障已排除、RIoT 里这台车还有未结束的订单：入口回 409，三条理由（没确认、车上有未完成订单、当前单没结束）一起列出；故障不动 | PASS | `409 / REMEDY_NOT_CONFIRMED + VEHICLE_ORDER_NOT_FINISHED + CURRENT_ORDER_NOT_ENDED / SuspectedBlocked` | `409 / FAULT_RECOVERY_REMEDY_NOT_CONFIRMED,FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED,FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED / SuspectedBlocked` |
| 条件齐全：入口回 200，结果 Cleared、处置 REBUILD_SCHEDULED；故障 Level None，ClearedReason 记着操作员 L2-OPERATOR-07；旧旅程不关闭、停在开往取货站，码 VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD，需求不释放（翻转自 #299 的「释放改派、旧旅程关闭」，依据 issuecomment-5787511271） | FAIL | `200 / Cleared / REBUILD_SCHEDULED / None / L2-OPERATOR-07 / 1 趟 AwaitingPickupArrival VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD / 需求未移除` | `200 / Cleared / RELEASED_FOR_REDISPATCH / None / FAULT_CLEARED_BY_OPERATOR:L2-OPERATOR-07 / 1 趟 Completed RELEASED_FOR_REDISPATCH / 需求已移除` |
| 延迟之后重建：同一趟旅程的取货停靠改指向一张服务端自己的新单（W2G-…），意图已确认，同一条需求、同一辆车；RIoT 上有这张单、指派给这辆车；没有新旅程 | FAIL | `新单号 W2G-… / CONFIRMED / 需求 dddf7a92-87d9-4e55-89d1-9dae5aafaa2b / BROKERX-L2-0001 / RIoT 1 张 / 1 趟旅程` | `W2G-dddf7a92-87d9-4e55-89d1-9dae5aafaa2b-PICKUP-1 / CONFIRMED / 需求 dddf7a92-87d9-4e55-89d1-9dae5aafaa2b / BROKERX-L2-0001 / RIoT 1 张 / 2 趟旅程` |
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
