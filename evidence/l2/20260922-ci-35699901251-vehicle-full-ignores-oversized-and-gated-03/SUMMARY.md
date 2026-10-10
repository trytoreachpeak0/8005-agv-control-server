# L2 场景证据：vehicle-full-ignores-oversized-and-gated

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T074242818Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T074242818Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求甲装满 FRONT、REAR 空着：车进入 CARGO_HOLDING_WAIT，满的一侧只有 FRONT | PASS | `CARGO_HOLDING_WAIT, ["FRONT"]` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, ["FRONT"]` |
| 需求乙（REAR 5 花篮，比整组还大）判 EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP、没有进来；又转五轮，REAR 仍不算满，车仍在持货等单 | PASS | `EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / not joined / CARGO_HOLDING_WAIT, full ["FRONT"]` | `EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / not joined / AwaitingStationDeparture CARGO_HOLDING_WAIT, full ["FRONT"]` |
| 需求丙（FRONT 5 花篮）同样判结构性装不下；状态与满的一侧都不变 | PASS | `EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / not joined / CARGO_HOLDING_WAIT, full ["FRONT"]` | `EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP / not joined / AwaitingStationDeparture CARGO_HOLDING_WAIT, full ["FRONT"]` |
| 经看板暂停 WIRE_TO_GATE：提交后 303 回到看板主页 | PASS | `303` | `303` |
| 暂停之后的需求丁（REAR 1 花篮，本来放得下）判 TASK_TYPE_HELD、没有进来；REAR 仍不算满，车仍在持货等单 | PASS | `TASK_TYPE_HELD / not joined / CARGO_HOLDING_WAIT, full ["FRONT"]` | `TASK_TYPE_HELD / not joined / AwaitingStationDeparture CARGO_HOLDING_WAIT, full ["FRONT"]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
