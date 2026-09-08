# L2 场景证据：route-graph-engine

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T090059065Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `b068eee4ffc0c727cf342d51e149a209ad59bbd2` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `0.1.1` |
| protocolReleaseIdentity.tag | `protocol-v0.1.1` |
| protocolReleaseIdentity.commit | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` |
| protocolReleaseIdentity.protocolVersion | `1` |
| protocolReleaseIdentity.profileId | `WIRE_TO_GATE_MVP` |
| protocolReleaseIdentity.manifestSha256 | `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f` |
| protocolReleaseIdentity.schemaBundleSha256 | `e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c` |
| protocolReleaseIdentity.vectorsSha256 | `fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T090059065Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 设计态读回了种子图的八条有向边 | PASS | `8` | `8` |
| 设计态读回了三个站点 | PASS | `3` | `3` |
| 运行态周期也跑过（移除集已读回，空是正常答案） | PASS | `(非空)` | `2026-09-08 09:01:07.9743404+00:00` |
| 边组指纹为空串且已取过一次（合成 RIoT 与 map25 一样没有边组） | PASS | `'' + 已取过` | `'' + 已取过` |
| 快照不是陈旧态 | PASS | `(无陈旧原因)` | `(null)` |
| 引擎开着时派车照常走通（可达性判据放行） | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 候选判定结果是 ACCEPTED，没有被任何 ROUTE_GRAPH_* 原因挡住 | PASS | `ACCEPTED` | `ACCEPTED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
