# L2 场景证据：task-type-binding-admits-bound-station

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T075451073Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T075451073Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 受理冻结的规则版本、绑定集版本等于当前生效版本，冻结的卸货站是 220 | PASS | `rule v1 / map-25 bindings v1 / dropoff 220` | `rule v1 / map-25 bindings v1 / dropoff 220` |
| 关卡腿的订单目标站是绑定的 220「关卡2」，不是地图上仍在的 210 | PASS | `220` | `220` |
| 旅程走完：Completed，全程两条 RIoT 单 | PASS | `Completed / 2 RIoT orders` | `Completed / 2 RIoT orders` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
