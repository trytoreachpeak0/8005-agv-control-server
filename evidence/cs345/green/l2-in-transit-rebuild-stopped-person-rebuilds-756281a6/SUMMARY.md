# L2 场景证据：in-transit-rebuild-stopped-person-rebuilds

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260923T173029624Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `756281a60282063dc9e6e5b2c75198a8188e6872` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260923T173029624Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 重建出来的单在窗口内又被取消：护栏三停住，旅程仍开往取货站、码 OWN_ORDER_REBUILD_STOPPED；只建过两张单；记录 STOPPED；需求未移除 | PASS | `AwaitingPickupArrival / OWN_ORDER_REBUILD_STOPPED / 2 张 / 未移除 / STOPPED REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW` | `AwaitingPickupArrival / OWN_ORDER_REBUILD_STOPPED / 2 张 / 移除 0 / STOPPED REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW` |
| 没确认已查明原因的请求被拒（409，FAULT_RECOVERY_REMEDY_NOT_CONFIRMED），旅程与单数不变 | PASS | `409 FAULT_RECOVERY_REMEDY_NOT_CONFIRMED / OWN_ORDER_REBUILD_STOPPED / 2 张` | `409 FAULT_RECOVERY_REMEDY_NOT_CONFIRMED / OWN_ORDER_REBUILD_STOPPED / 2 张` |
| 署名、确认的人工重建被受理（200 RebuildRequested / REBUILD_SCHEDULED），停住的记录原行重开、署上这个人、保留停住原因 | PASS | `200 RebuildRequested REBUILD_SCHEDULED / L2-OPERATOR-345 / REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW / PENDING\|ORDERING\|REBUILT` | `200 RebuildRequested REBUILD_SCHEDULED / L2-OPERATOR-345 / REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW / PENDING` |
| 人工重建之后：取货停靠（同序位、同站）改指向第三张服务端自己的单，意图已确认，需求与车不变、目标站不变，旅程码清掉；RIoT 上有这张单、指派给同一辆车 | PASS | `第三张 W2G-… / CONFIRMED / 8d99c6ee-a2c9-4915-b84b-6539b470ff28 / BROKERX-L2-0001 / 站 12 / 序位 1 / AwaitingPickupArrival (无码) / RIoT 1 张` | `W2G-8d99c6ee-a2c9-4915-b84b-6539b470ff28-REBUILD-1-2 / CONFIRMED / 8d99c6ee-a2c9-4915-b84b-6539b470ff28 / BROKERX-L2-0001 / 站 12 / 序位 1 / AwaitingPickupArrival () / RIoT 1 张` |
| 同一请求再来一次：200 AlreadyDone，不再建单（恰好三张意图、一趟旅程、需求未移除）；全程没有发出任何订单命令或急停 | PASS | `200 AlreadyDone / 3 张 / 1 趟 / 未移除 / 0 条命令 / 0 次调用` | `200 AlreadyDone / 3 张 / 1 趟 / 移除 0 / 0 条命令 / 0 次调用` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
