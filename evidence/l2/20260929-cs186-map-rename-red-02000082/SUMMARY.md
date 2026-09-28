# L2 场景证据：map-rename-holds-all-task-types

结论：**FAIL**

失败原因：ControlServer.FieldOps accept-map-name exited with 1: {"command":"accept-map-name","outcome":"REJECTED","mapId":25,"previousName":null,"acceptedName":null,"violations":[],"auditRecordId":""}

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260928T160403204Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `0200008215c9dde17d1ae137ab5c5a0e2a074117` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260928T160403204Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 地图列表读不到的几轮不算改名：没有暂停，基线与读失败之前相同 | PASS | `(none); (no baseline)` | `(none); (no baseline)` |
| 改名之前（地图名 老厂前线new_wk）WIRE_TO_GATE 需求照常受理、建出关卡单并走完 | PASS | `TO_GATE CONFIRMED / Completed` | `TO_GATE CONFIRMED / Completed` |
| 改名之后 WIRE_TO_GATE 与 STAGING_TO_WIRE 各恰好一条来源目录变化、原因码 MAP_RENAMED 的暂停，没有别的暂停 | FAIL | `STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED` | `(none)` |
| 改名之后车空闲，新的 WIRE_TO_GATE 需求连续几轮都不受理，原因码 TASK_TYPE_HELD | FAIL | `no journey / TASK_TYPE_HELD` | `AwaitingPickupArrival / DEMAND_ALREADY_ACCEPTED` |
| 看板暂停卡片上两个任务类型都显示已暂停、来源目录变化、原因地图改名 | FAIL | `WIRE_TO_GATE、STAGING_TO_WIRE: 已暂停 目录变化 地图改名` | `WIRE_TO_GATE 在本图需求集合 210 关卡 正常 无 暂停 \| STAGING_TO_WIRE 在本图需求集合 230 派工待送取货 正常 无 暂停` |
| 基线仍是改名前的名称，新名称待接受 | FAIL | `name=老厂前线new_wk pending=老厂前线new_wk-L2-20260928T160403204Z` | `(no baseline)` |
| 接受新名之前解除 WIRE_TO_GATE 的暂停被拒，原因码 MAP_RENAME_NOT_ACCEPTED，两条暂停都还在 | FAIL | `refused MAP_RENAME_NOT_ACCEPTED; 2 holds` | `ControlServer.FieldOps release-task-type-station-hold exited with 1: {"command":"release-task-type-station-hold","outcome":"REJECTED","mapId":25,"taskType":"WIRE_TO_GATE","violations":[{"reasonCode":"TASK_TYPE_HOLD_NOTHING_TO_RELEASE","taskType":"WIRE_TO_GATE","stationRiotId":null,"detail":"Map 25 WIRE_TO_GATE has no unreleased manual or catalog-change hold."}],"released":[],"auditRecordId":"20bd11e14f3144098a5de9f5290c7bad"}; (none)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
