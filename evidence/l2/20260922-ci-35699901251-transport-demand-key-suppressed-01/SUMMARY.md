# L2 场景证据：transport-demand-key-suppressed

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T073407750Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T073407750Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| D1 被站点期限本地取消，同一次提交里按业务键写下抑制：键 S1|WIRE_TO_GATE、需求 D1、码 CANCELLED_BY_STATION_TIMEOUT | PASS | `CANCELLED_BY_STATION_TIMEOUT / 1 row L2-TDK-20260922T073407750Z-S1\|WIRE_TO_GATE 362a63c6-07cb-44d1-8fcf-381c878849eb CANCELLED_BY_STATION_TIMEOUT` | `CANCELLED_BY_STATION_TIMEOUT / 1 row(s) L2-TDK-20260922T073407750Z-S1\|WIRE_TO_GATE 362a63c6-07cb-44d1-8fcf-381c878849eb CANCELLED_BY_STATION_TIMEOUT` |
| D2（同一个 S1、新 DemandId）被判定过但从未受理：积压原因 TRANSPORT_DEMAND_KEY_SUPPRESSED，没有受理行、没有订单意图 | PASS | `TRANSPORT_DEMAND_KEY_SUPPRESSED / not accepted / 0 accepted rows / 0 intents` | `TRANSPORT_DEMAND_KEY_SUPPRESSED / AcceptedAt= / 0 accepted rows / 0 intents` |
| /api/dashboard/dispatch-backlog 列出 D2，原因码 TRANSPORT_DEMAND_KEY_SUPPRESSED 带中文说明 | PASS | `one row / TRANSPORT_DEMAND_KEY_SUPPRESSED / Chinese description` | `TRANSPORT_DEMAND_KEY_SUPPRESSED / 这个子批次的这类任务已在本地取消过，按业务键永久不再执行；MES 换了新的需求号也一样` |
| 排在 D2 后面的无关需求 D3 在同一段时间里被受理——整轮没有被 D2 卡住 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| D3 走完：旅程 Completed、需求 Succeeded | PASS | `Completed / Succeeded` | `Completed / Succeeded` |
| D3 走完、车空下来之后 D2 被再判一次，仍是 TRANSPORT_DEMAND_KEY_SUPPRESSED、仍未受理 | PASS | `TRANSPORT_DEMAND_KEY_SUPPRESSED / not accepted / judged after 2026-09-22T07:34:54.1969920+00:00` | `TRANSPORT_DEMAND_KEY_SUPPRESSED / AcceptedAt= / LastSeenAt=2026-09-22T07:34:55.2036874+00:00` |
| GONE 半边：D4 在受理前离开目录（DEMAND_LEFT_CATALOG），同一个 S4 的新 DemandId D5 照常受理；S4 没有抑制行，全库仍只有 D1 那一条 | PASS | `DEMAND_LEFT_CATALOG / D5 AwaitingPickupArrival / 0 rows on L2-TDK-20260922T073407750Z-S4\|WIRE_TO_GATE / 1 row in all` | `DEMAND_LEFT_CATALOG (judged first as ROUTE_GRAPH_NEVER_REFRESHED) / D5 AwaitingPickupArrival / 0 rows on L2-TDK-20260922T073407750Z-S4\|WIRE_TO_GATE / 1 in all` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
