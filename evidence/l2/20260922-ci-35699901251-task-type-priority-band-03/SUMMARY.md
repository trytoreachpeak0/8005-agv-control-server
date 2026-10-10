# L2 场景证据：task-type-priority-band

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T080516563Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T080516563Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车空出来之后先受理刚建的 STAGING_TO_WIRE，而不是等了 20 分钟的 WIRE_TO_GATE（REQ-0202：STAGING_TO_WIRE 独占最高初始带） | PASS | `STAGING` | `STAGING` |
| STAGING 受理之后，OLD 被重新判过、仍未受理（车已经给了 STAGING） | PASS | `re-judged after 2026-09-22T08:06:08.1138151+00:00, not accepted` | `LastSeenAt 2026-09-22 08:06:09.0546571+00:00, AcceptedAt '', reason ROUTE_GRAPH_NEVER_REFRESHED` |
| 本区阈值未配置：OLD 等了 20 分钟也不升级，两条都没有升级告警标记（REQ-0203 降级） | PASS | `(none)` | `(none)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
