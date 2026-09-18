# L2 场景证据：area-assignment-unmapped-silent

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T152437563Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35361077376-1\_stage\l2-20260918T152437563Z-slot4` |
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
