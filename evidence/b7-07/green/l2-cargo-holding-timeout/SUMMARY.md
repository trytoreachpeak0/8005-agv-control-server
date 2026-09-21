# L2 场景证据：cargo-holding-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T092140518Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `1dc9a178da7b8fe559afa6234a727aaa70950090` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T092140518Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求甲（1 花篮）装完、两侧都没满：车进入 CARGO_HOLDING_WAIT，起算点已落库 | PASS | `CARGO_HOLDING_WAIT, started` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, started '2026-09-21 09:22:14.3487626+00:00'` |
| 起算后约 30 秒（站点等待 10 秒早已过去，期限 40 秒未到）：车仍在站上持货，关卡腿没有建单 | PASS | `before deadline / CARGO_HOLDING_WAIT / AwaitingStationDeparture / 0 gate intents` | `before deadline (2026-09-21T09:22:44.3699231+00:00) / AwaitingStationDeparture CARGO_HOLDING_WAIT / 0 gate intents` |
| 期限一到装货阶段关闭，理由 CARGO_HOLDING_TIMEOUT；发给车的那张关闭快照不早于期限 | PASS | `CLOSED/CARGO_HOLDING_TIMEOUT at or after 2026-09-21T09:22:54.3487626+00:00` | `AwaitingDepartureSafety CLOSED/CARGO_HOLDING_TIMEOUT, snapshot CLOSED/CARGO_HOLDING_TIMEOUT at 2026-09-21T09:22:55.3165389+00:00` |
| 关闭之后车带着已装的货离站：关卡腿建了单 | PASS | `>= 1` | `1` |
| 关闭之后发的需求戊没有进这趟旅程，积压理由 LOADING_PHASE_CLOSED | PASS | `no journey / LOADING_PHASE_CLOSED` | `no journey / LOADING_PHASE_CLOSED` |
| WAIT 快照与之后的 CLOSED 快照都带 cargoHoldingDeadlineAt，等于起算点 + 40 秒 | PASS | `2026-09-21T09:22:54.3487626+00:00` | `2026-09-21T09:22:54.3487626+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
