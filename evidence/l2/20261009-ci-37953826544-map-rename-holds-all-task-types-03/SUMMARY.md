# L2 场景证据：map-rename-holds-all-task-types

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T155340409Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T155340409Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 地图列表读不到的几轮不算改名：窗口内读过列表至少一次、答 500 至少一次，没有暂停，基线与读失败之前相同 | PASS | `reads >= 1, 500s >= 1; (none); name=老厂前线new_wk pending=(none)` | `reads 3, 500s 3; (none); name=老厂前线new_wk pending=(none)` |
| 改名之前（地图名 老厂前线new_wk）WIRE_TO_GATE 需求照常受理、建出关卡单并走完 | PASS | `TO_GATE CONFIRMED / Completed` | `TO_GATE CONFIRMED / Completed` |
| 改名之后 WIRE_TO_GATE 与 STAGING_TO_WIRE 各恰好一条来源目录变化、原因码 MAP_RENAMED 的暂停，没有别的暂停 | PASS | `STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED` | `STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED` |
| 改名之后车空闲，新的 WIRE_TO_GATE 需求连续几轮都不受理，原因码 TASK_TYPE_HELD | PASS | `no journey / TASK_TYPE_HELD` | `no journey / TASK_TYPE_HELD` |
| 看板暂停卡片上两个任务类型都显示已暂停、来源目录变化、原因地图改名 | PASS | `WIRE_TO_GATE、STAGING_TO_WIRE: 已暂停 目录变化 地图改名` | `WIRE_TO_GATE 在本图需求集合 210 关卡 已暂停 目录变化，自 2026-10-09T15:54:47.0377837+00:00，地图改名 暂停 \| STAGING_TO_WIRE 在本图需求集合 230 派工待送取货 已暂停 目录变化，自 2026-10-09T15:54:47.0377837+00:00，地图改名 暂停` |
| 基线仍是改名前的名称，新名称待接受 | PASS | `name=老厂前线new_wk pending=老厂前线new_wk-L2-20261009T155340409Z` | `name=老厂前线new_wk pending=老厂前线new_wk-L2-20261009T155340409Z` |
| 接受新名之前解除 WIRE_TO_GATE 的暂停被拒，原因码 MAP_RENAME_NOT_ACCEPTED，两条暂停都还在 | PASS | `refused MAP_RENAME_NOT_ACCEPTED; 2 holds` | `ControlServer.FieldOps release-task-type-station-hold exited with 1: {"command":"release-task-type-station-hold","outcome":"REJECTED","mapId":25,"taskType":"WIRE_TO_GATE","violations":[{"reasonCode":"MAP_RENAME_NOT_ACCEPTED","taskType":"WIRE_TO_GATE","stationRiotId":null,"detail":"Map 25 was renamed from '老厂前线new_wk' to '老厂前线new_wk-L2-20261009T155340409Z' and the new name has not been accepted; run accept-map-name first."}],"released":[],"auditRecordId":"416b57f4365c48d297d557d25f4f2d54"}; STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED` |
| accept-map-name 与两次 release-task-type-station-hold 都成功：没有未解除的暂停，基线换成新名、没有待接受的名称 | PASS | `accept OK, 2 releases OK; (none); name=老厂前线new_wk-L2-20261009T155340409Z pending=(none)` | `accept OK, releases OK/OK; (none); name=老厂前线new_wk-L2-20261009T155340409Z pending=(none)` |
| 接受新名并解除暂停之后，等着的那条需求受理、建出关卡单；新名称连读多轮也不再加暂停 | PASS | `TO_GATE CONFIRMED; (none)` | `TO_GATE CONFIRMED; (none)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
