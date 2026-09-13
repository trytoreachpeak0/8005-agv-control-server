# L2 场景证据：route-graph-staleness

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T085608169Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `95294f9e0c36cea2bd48fc46b29c8a91e77a0f03` |
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
| stageRoot | `C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Temp\l2-20260913T085608169Z` |
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
