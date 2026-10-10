# L2 场景证据：plan-reorder-and-drop-cancelled

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T080722915Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T080722915Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 三条需求都在同一趟旅程上（A 受理、B 与 C 追加） | PASS | `3` | `3` |
| A 终结（扫码前取消），它自己的卸货停靠（机台 12）因为没有剩余作业被删 | PASS | `TERMINATED / REMOVED` | `TERMINATED / REMOVED` |
| 车上最后一版计划里没有机台 12 那条腿，计划修订号前进（整体替换下发，ADR-cross-0053） | PASS | `无 N1-3_N1-7 / 修订号 > 4（到站那一版）` | `派工待送取货,派工待送取货,C15-13,N1-5 / 修订号 5` |
| 当前下一站（A 的取货停靠）没有被删（REQ-0197：当前下一站不删不换） | PASS | `不是 REMOVED` | `PENDING` |
| B、C 的归属仍在，它们的卸货腿仍在最后一版计划里 | PASS | `2 / 2` | `2 / 2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
