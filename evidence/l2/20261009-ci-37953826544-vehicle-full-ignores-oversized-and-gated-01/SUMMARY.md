# L2 场景证据：vehicle-full-ignores-oversized-and-gated

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T155843598Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T155843598Z-slot3` |
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
