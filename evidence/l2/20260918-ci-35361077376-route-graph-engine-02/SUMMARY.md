# L2 场景证据：route-graph-engine

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T152612735Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35361077376-1\_stage\l2-20260918T152612735Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 设计态读回了种子图的八条有向边 | PASS | `8` | `8` |
| 设计态读回了三个站点 | PASS | `3` | `3` |
| 运行态周期也跑过（移除集已读回，空是正常答案） | PASS | `(非空)` | `2026-09-18 15:26:24.7429347+00:00` |
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
