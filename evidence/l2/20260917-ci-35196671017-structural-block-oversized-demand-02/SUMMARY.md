# L2 场景证据：structural-block-oversized-demand

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260917T081420156Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `03ec8a8c8cb3f45098de38b7ffb6c4a9d6f33d08` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Temp\l2-20260917T081420156Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：本车 FRONT 组有物理仓位，需求要 5 个花篮，比该组物理仓位数多一个（默认模型下 5 个） | PASS | `FRONT physical slots < 5 baskets` | `FRONT physical slots 4, baskets 5 (20 boxes)` |
| StructuralDispatchBlocks 恰好一行，原因码 EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP，仍成立；积压原因同码 | PASS | `1 row / EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / uncleared / backlog EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP` | `1 rows / EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / ClearedAt= / backlog EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP` |
| 首次形成时间就是需求出现后的第一轮：等于积压行首次被判定的时间 | PASS | `FirstRaisedAt = backlog FirstSeenAt (09/17/2026 08:14:35 +00:00)` | `FirstRaisedAt 09/17/2026 08:14:35 +00:00` |
| 明细记下花篮数 5、分组 FRONT 与全车队该组最大物理仓位数 4 | PASS | `5 / FRONT / 4` | `5 / FRONT / 4` |
| 再转三轮仍是同一行：首次形成时间不变、最近仍成立时间前进、没有第二行、未清除 | PASS | `1 row / FirstRaisedAt 09/17/2026 08:14:35 +00:00 / LastSeenAt > 09/17/2026 08:14:35 +00:00 / uncleared` | `1 rows / FirstRaisedAt 09/17/2026 08:14:35 +00:00 / LastSeenAt 09/17/2026 08:14:38 +00:00 / ClearedAt=` |
| 服务端日志里这条需求的「形成」Warning 只有一条：刷新不重复写 | PASS | `1 record` | `1 records` |
| 不派车、不部分装入：没有受理行、没有旅程、没有建 RIoT 单 | PASS | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` |
| 删掉需求后告警清除：表里仍只有那一行、已记清除时间、首次形成时间不变，需求从未受理 | PASS | `1 row / cleared / same FirstRaisedAt / never accepted` | `1 rows / ClearedAt=2026-09-17 08:14:39.4819617+00:00 / FirstRaisedAt 09/17/2026 08:14:35 +00:00 / accepted 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
