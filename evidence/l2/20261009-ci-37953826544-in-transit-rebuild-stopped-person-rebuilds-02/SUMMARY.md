# L2 场景证据：in-transit-rebuild-stopped-person-rebuilds

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162113314Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162113314Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 重建出来的单在窗口内又被取消：护栏三停住，旅程仍开往取货站、码 OWN_ORDER_REBUILD_STOPPED；只建过两张单；记录 STOPPED；需求未移除 | PASS | `AwaitingPickupArrival / OWN_ORDER_REBUILD_STOPPED / 2 张 / 未移除 / STOPPED REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW` | `AwaitingPickupArrival / OWN_ORDER_REBUILD_STOPPED / 2 张 / 移除 0 / STOPPED REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW` |
| 没确认已查明原因的请求被拒（409，FAULT_RECOVERY_REMEDY_NOT_CONFIRMED），旅程与单数不变 | PASS | `409 FAULT_RECOVERY_REMEDY_NOT_CONFIRMED / OWN_ORDER_REBUILD_STOPPED / 2 张` | `409 FAULT_RECOVERY_REMEDY_NOT_CONFIRMED / OWN_ORDER_REBUILD_STOPPED / 2 张` |
| 署名、确认的人工重建被受理（200 RebuildRequested / REBUILD_SCHEDULED），停住的记录原行重开、署上这个人、保留停住原因 | PASS | `200 RebuildRequested REBUILD_SCHEDULED / L2-OPERATOR-345 / REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW / PENDING\|ORDERING\|REBUILT` | `200 RebuildRequested REBUILD_SCHEDULED / L2-OPERATOR-345 / REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW / PENDING` |
| 人工重建之后：取货停靠（同序位、同站）改指向第三张服务端自己的单，意图已确认，需求与车不变、目标站不变，旅程码清掉；RIoT 上有这张单、指派给同一辆车 | PASS | `第三张 W2G-… / CONFIRMED / 9ed64b12-d717-4967-955f-b3b4dfd90768 / BROKERX-L2-0001 / 站 12 / 序位 1 / AwaitingPickupArrival (无码) / RIoT 1 张` | `W2G-9ed64b12-d717-4967-955f-b3b4dfd90768-REBUILD-1-2 / CONFIRMED / 9ed64b12-d717-4967-955f-b3b4dfd90768 / BROKERX-L2-0001 / 站 12 / 序位 1 / AwaitingPickupArrival () / RIoT 1 张` |
| 同一请求再来一次：200 AlreadyDone，不再建单（恰好三张意图、一趟旅程、需求未移除）；全程没有发出任何订单命令或急停 | PASS | `200 AlreadyDone / 3 张 / 1 趟 / 未移除 / 0 条命令 / 0 次调用` | `200 AlreadyDone / 3 张 / 1 趟 / 移除 0 / 0 条命令 / 0 次调用` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
