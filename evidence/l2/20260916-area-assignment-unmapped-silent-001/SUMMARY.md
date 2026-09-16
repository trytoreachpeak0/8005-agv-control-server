# L2 场景证据：area-assignment-unmapped-silent

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260916T091531868Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `78f4321b6c254e4df0320e6cbe89212bb8e5179d` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| protocolReleaseIdentity.schemaBundleSha256 | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260916T091531868Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 分区归属表导入成功：当前版本只含 N1-3 与 T5-2，N1-7 不在表里 | PASS | `OK / OK / v1 / N1-3,T5-2` | `OK / OK / v1 / N1-3,T5-2` |
| N1-7（N 开头、地图上有站点，但表里没有）被白名单挡住：积压原因 OUT_OF_SCOPE_AREA，未受理 | PASS | `OUT_OF_SCOPE_AREA / not accepted / 0 rows` | `OUT_OF_SCOPE_AREA / AcceptedAt= / 0 rows` |
| T5-2（T 开头、表里有）通过白名单：被后面的判据挡住，原因不再是 OUT_OF_SCOPE_AREA | PASS | `any reason but OUT_OF_SCOPE_AREA (no station on the map: AREA_STATION_NOT_FOUND)` | `AREA_STATION_NOT_FOUND` |
| N1-3 正常受理：冻结行的版本等于当前版本、快照是那一版的快照，路线调度区取自表 | PASS | `AwaitingPickupArrival / frozen v1 = current / own snapshot / MAP-25-WIRE_TO_GATE` | `AwaitingPickupArrival / frozen v1, current v1 / True / MAP-25-WIRE_TO_GATE` |
| N1-3 受理之后，N1-7 仍然一行受理记录、旅程、订单意图、冻结行都没有，积压原因仍是 OUT_OF_SCOPE_AREA | PASS | `0 rows / OUT_OF_SCOPE_AREA` | `0 rows / OUT_OF_SCOPE_AREA` |
| StructuralDispatchBlocks 无行：未映射 AREA 不是结构性派车阻断 | PASS | `0` | `0` |
| 服务端日志里没有提到 N1-7 这条需求（两种写法）或 OUT_OF_SCOPE_AREA 的 Warning 及以上记录 | PASS | `0 records` | `0 records` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
