# L2 场景证据：route-graph-staleness

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T132613107Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `50dc987d680a33c3f8732619a91f5761453ddd63` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35445347285-1\_stage\l2-20260919T132613107Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 基线快照建立：设计态、边组指纹、动态代价都取过一次，且不陈旧 | PASS | `(无陈旧原因)` | `(null)` |
| 边组指纹是空串而不是「没取过」（合成 RIoT 与 map25 一样一个边组都没有） | PASS | `'' + 已取过` | `'' + 已取过` |
| 触发一（超 TTL）：快照进陈旧态，原因是 ROUTE_GRAPH_RUNTIME_STATE_EXPIRED | PASS | `ROUTE_GRAPH_RUNTIME_STATE_EXPIRED` | `ROUTE_GRAPH_RUNTIME_STATE_EXPIRED` |
| 触发一 fail-closed：候选被挡住，原因直指引擎而不是笼统的「无候选」 | PASS | `ROUTE_GRAPH_RUNTIME_STATE_EXPIRED` | `ROUTE_GRAPH_RUNTIME_STATE_EXPIRED` |
| 触发二（动态代价由空变非空）：原因是 ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED | PASS | `ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED` | `ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED` |
| 刷新恢复正常之后它仍然陈旧：动态代价这条不因等待而解除 | PASS | `ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED` | `ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED` |
| 触发二 fail-closed：阻断原因跟着换成这一条 | PASS | `ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED` | `ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED` |
| 触发三（边组指纹变化）：原因是 ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED | PASS | `ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED` | `ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED` |
| 指纹确实换了值，而不是只翻了个标志位 | PASS | `≠ ''` | `'61771dd33f0dcf0a1d43740415958f2f8552c77eab15832471948d1cf407b965'` |
| 触发三 fail-closed：阻断原因跟着换成这一条 | PASS | `ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED` | `ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED` |
| 三条陈旧全程没有建出任何 journey | PASS | `0` | `0` |
| 一张 RIoT move 单都没建 | PASS | `0` | `0` |
| 需求始终没有被受理 | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
