# L2 场景证据：cargo-holding-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T154927629Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T154927629Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求甲（1 花篮）装完、两侧都没满：车进入 CARGO_HOLDING_WAIT，起算点已落库 | PASS | `CARGO_HOLDING_WAIT, started` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, started '2026-10-09 15:49:54.1052353+00:00'` |
| 起算后约 30 秒（站点等待 10 秒早已过去，期限 40 秒未到）：车仍在站上持货，关卡腿没有建单 | PASS | `before deadline / CARGO_HOLDING_WAIT / AwaitingStationDeparture / 0 gate intents` | `before deadline (2026-10-09T15:50:24.1214809+00:00) / AwaitingStationDeparture CARGO_HOLDING_WAIT / 0 gate intents` |
| 期限一到装货阶段关闭，理由 CARGO_HOLDING_TIMEOUT；发给车的那张关闭快照不早于期限 | PASS | `CLOSED/CARGO_HOLDING_TIMEOUT at or after 2026-10-09T15:50:34.1052353+00:00` | `AwaitingDepartureSafety CLOSED/CARGO_HOLDING_TIMEOUT, snapshot CLOSED/CARGO_HOLDING_TIMEOUT at 2026-10-09T15:50:34.1614844+00:00` |
| 关闭之后车带着已装的货离站：关卡腿建了单 | PASS | `>= 1` | `1` |
| 关闭之后发的需求戊没有进这趟旅程，积压理由 LOADING_PHASE_CLOSED | PASS | `no journey / LOADING_PHASE_CLOSED` | `no journey / LOADING_PHASE_CLOSED` |
| WAIT 快照与之后的 CLOSED 快照都带 cargoHoldingDeadlineAt，等于起算点 + 40 秒 | PASS | `2026-10-09T15:50:34.1052353+00:00` | `2026-10-09T15:50:34.1052353+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
