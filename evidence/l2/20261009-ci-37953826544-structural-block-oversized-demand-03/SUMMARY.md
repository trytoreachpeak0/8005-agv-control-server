# L2 场景证据：structural-block-oversized-demand

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163225466Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163225466Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：本车 FRONT 组有物理仓位，需求要 5 个花篮，比该组物理仓位数多一个（默认模型下 5 个） | PASS | `FRONT physical slots < 5 baskets` | `FRONT physical slots 4, baskets 5 (20 boxes)` |
| StructuralDispatchBlocks 恰好一行，原因码 EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP，仍成立；积压原因同码 | PASS | `1 row / EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / uncleared / backlog EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP` | `1 rows / EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / ClearedAt= / backlog EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP` |
| 首次形成时间就是需求出现后的第一轮：等于积压行首次被判定的时间 | PASS | `FirstRaisedAt = backlog FirstSeenAt (10/09/2026 16:32:47 +00:00)` | `FirstRaisedAt 10/09/2026 16:32:47 +00:00` |
| 明细记下花篮数 5、分组 FRONT 与全车队该组最大物理仓位数 4 | PASS | `5 / FRONT / 4` | `5 / FRONT / 4` |
| 再转三轮仍是同一行：首次形成时间不变、最近仍成立时间前进、没有第二行、未清除 | PASS | `1 row / FirstRaisedAt 10/09/2026 16:32:47 +00:00 / LastSeenAt > 10/09/2026 16:32:47 +00:00 / uncleared` | `1 rows / FirstRaisedAt 10/09/2026 16:32:47 +00:00 / LastSeenAt 10/09/2026 16:32:50 +00:00 / ClearedAt=` |
| 服务端日志里这条需求的「形成」Warning 只有一条：刷新不重复写 | PASS | `1 record` | `1 records` |
| 不派车、不部分装入：没有受理行、没有旅程、没有建 RIoT 单 | PASS | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` |
| 删掉需求后告警清除：表里仍只有那一行、已记清除时间、首次形成时间不变，需求从未受理 | PASS | `1 row / cleared / same FirstRaisedAt / never accepted` | `1 rows / ClearedAt=2026-10-09 16:32:51.8111141+00:00 / FirstRaisedAt 10/09/2026 16:32:47 +00:00 / accepted 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
