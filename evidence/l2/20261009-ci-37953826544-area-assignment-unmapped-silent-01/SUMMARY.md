# L2 场景证据：area-assignment-unmapped-silent

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T165552405Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T165552405Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 默认前置按边车导入了分区归属表：只有一版，只含 N1-3 与 T5-2，N1-7 不在表里 | PASS | `OK / 1 version / N1-3/MAP-25-WIRE_TO_GATE/FRONT,T5-2/MAP-25-WIRE_TO_GATE/REAR` | `OK / 1 version / N1-3/MAP-25-WIRE_TO_GATE/FRONT,T5-2/MAP-25-WIRE_TO_GATE/REAR` |
| N1-7（N 开头、地图上有站点，但表里没有）被白名单挡住：积压原因 OUT_OF_SCOPE_AREA，未受理 | PASS | `OUT_OF_SCOPE_AREA / not accepted / 0 rows` | `OUT_OF_SCOPE_AREA / AcceptedAt= / 0 rows` |
| T5-2（T 开头、表里有）通过白名单：被后面的判据挡住，原因不再是 OUT_OF_SCOPE_AREA | PASS | `any reason but OUT_OF_SCOPE_AREA (no station on the map: AREA_STATION_NOT_FOUND)` | `AREA_STATION_NOT_FOUND` |
| N1-3 正常受理：冻结行的版本等于当前版本、快照是那一版的快照，路线调度区取自表 | PASS | `AwaitingPickupArrival / frozen v1 = current / own snapshot / MAP-25-WIRE_TO_GATE` | `AwaitingPickupArrival / frozen v1, current v1 / True / MAP-25-WIRE_TO_GATE` |
| N1-3 受理之后，N1-7 仍然一行受理记录、旅程、订单意图、冻结行都没有，积压原因仍是 OUT_OF_SCOPE_AREA | PASS | `0 rows / OUT_OF_SCOPE_AREA` | `0 rows / OUT_OF_SCOPE_AREA` |
| StructuralDispatchBlocks 没有 N1-7 这条需求的行：未映射 AREA 不是结构性派车阻断 | PASS | `0` | `0` |
| 服务端日志里没有提到 N1-7 这条需求（两种写法）或 OUT_OF_SCOPE_AREA 的 Warning 及以上记录 | PASS | `0 records` | `0 records` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
