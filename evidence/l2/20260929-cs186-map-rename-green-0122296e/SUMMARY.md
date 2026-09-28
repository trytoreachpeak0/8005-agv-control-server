# L2 场景证据：map-rename-holds-all-task-types

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260928T203236435Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `0122296e02d89b66eaa9cb680687f13db0b12b3c` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260928T203236435Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 地图列表读不到的几轮不算改名：窗口内读过列表至少一次、答 500 至少一次，没有暂停，基线与读失败之前相同 | PASS | `reads >= 1, 500s >= 1; (none); name=老厂前线new_wk pending=(none)` | `reads 3, 500s 3; (none); name=老厂前线new_wk pending=(none)` |
| 改名之前（地图名 老厂前线new_wk）WIRE_TO_GATE 需求照常受理、建出关卡单并走完 | PASS | `TO_GATE CONFIRMED / Completed` | `TO_GATE CONFIRMED / Completed` |
| 改名之后 WIRE_TO_GATE 与 STAGING_TO_WIRE 各恰好一条来源目录变化、原因码 MAP_RENAMED 的暂停，没有别的暂停 | PASS | `STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED` | `STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED` |
| 改名之后车空闲，新的 WIRE_TO_GATE 需求连续几轮都不受理，原因码 TASK_TYPE_HELD | PASS | `no journey / TASK_TYPE_HELD` | `no journey / TASK_TYPE_HELD` |
| 看板暂停卡片上两个任务类型都显示已暂停、来源目录变化、原因地图改名 | PASS | `WIRE_TO_GATE、STAGING_TO_WIRE: 已暂停 目录变化 地图改名` | `WIRE_TO_GATE 在本图需求集合 210 关卡 已暂停 目录变化，自 2026-09-28T20:34:43.8029772+00:00，地图改名 暂停 \| STAGING_TO_WIRE 在本图需求集合 230 派工待送取货 已暂停 目录变化，自 2026-09-28T20:34:43.8029772+00:00，地图改名 暂停` |
| 基线仍是改名前的名称，新名称待接受 | PASS | `name=老厂前线new_wk pending=老厂前线new_wk-L2-20260928T203236435Z` | `name=老厂前线new_wk pending=老厂前线new_wk-L2-20260928T203236435Z` |
| 接受新名之前解除 WIRE_TO_GATE 的暂停被拒，原因码 MAP_RENAME_NOT_ACCEPTED，两条暂停都还在 | PASS | `refused MAP_RENAME_NOT_ACCEPTED; 2 holds` | `ControlServer.FieldOps release-task-type-station-hold exited with 1: {"command":"release-task-type-station-hold","outcome":"REJECTED","mapId":25,"taskType":"WIRE_TO_GATE","violations":[{"reasonCode":"MAP_RENAME_NOT_ACCEPTED","taskType":"WIRE_TO_GATE","stationRiotId":null,"detail":"Map 25 was renamed from '老厂前线new_wk' to '老厂前线new_wk-L2-20260928T203236435Z' and the new name has not been accepted; run accept-map-name first."}],"released":[],"auditRecordId":"57fba7fce0e94111b2d5ef32c2c0c8b1"}; STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED` |
| accept-map-name 与两次 release-task-type-station-hold 都成功：没有未解除的暂停，基线换成新名、没有待接受的名称 | PASS | `accept OK, 2 releases OK; (none); name=老厂前线new_wk-L2-20260928T203236435Z pending=(none)` | `accept OK, releases OK/OK; (none); name=老厂前线new_wk-L2-20260928T203236435Z pending=(none)` |
| 接受新名并解除暂停之后，等着的那条需求受理、建出关卡单；新名称连读多轮也不再加暂停 | PASS | `TO_GATE CONFIRMED; (none)` | `TO_GATE CONFIRMED; (none)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
