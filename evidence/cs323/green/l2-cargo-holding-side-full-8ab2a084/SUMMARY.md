# L2 场景证据：cargo-holding-side-full

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T152106120Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `8ab2a084ec05c28894de3fd54176cb96ce5e0eb9` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260922T152106120Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求甲装完、FRONT 已无空仓，但 REAR 还空着：车进入持货等单（CARGO_HOLDING_WAIT），不是 VEHICLE_FULL | PASS | `CARGO_HOLDING_WAIT, started` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, started '2026-09-22 15:21:45.3492212+00:00'` |
| 需求乙追加进了持货等单的这辆车（同一趟旅程） | PASS | `journey:02e92f2a-2c8c-4ba7-8851-2a3ff9b7bcf4` | `journey:02e92f2a-2c8c-4ba7-8851-2a3ff9b7bcf4` |
| 需求乙装完、REAR 还有两个空仓：仍是 CARGO_HOLDING_WAIT，起算点没有因为新停靠重置 | PASS | `CARGO_HOLDING_WAIT, started 2026-09-22 15:21:45.3492212+00:00` | `CARGO_HOLDING_WAIT (LOADED), started 2026-09-22 15:21:45.3492212+00:00` |
| 又转了五轮，REAR 仍有空仓且没有「只因本车货物装不下」的候选：状态仍是 CARGO_HOLDING_WAIT，FRONT 单侧满不算整车满 | PASS | `CARGO_HOLDING_WAIT` | `AwaitingStationDeparture CARGO_HOLDING_WAIT full sides ["FRONT"]` |
| 需求丁（3 花篮）只因本车货物占着 REAR 而装不下：车进入 VEHICLE_FULL，满的两侧是 FRONT 与 REAR | PASS | `VEHICLE_FULL, ["FRONT","REAR"]` | `AwaitingStationDeparture VEHICLE_FULL, ["FRONT","REAR"], D backlog SLOT_GROUP_OCCUPIED_BY_OWN_CARGO` |
| 车离开最后一个装货停靠：装货阶段关闭，理由 VEHICLE_FULL | PASS | `CLOSED/VEHICLE_FULL` | `AwaitingGateArrival CLOSED/VEHICLE_FULL` |
| 需求丁没有进这趟旅程，关闭之后它的积压理由是 LOADING_PHASE_CLOSED | PASS | `no journey / LOADING_PHASE_CLOSED` | `no journey / LOADING_PHASE_CLOSED` |
| 车辆业务状态快照的修订号不重号（车载端号同内容不同会当场拆会话） | PASS | `7 distinct` | `7 of 7` |
| 车收到的装货阶段依次是：到站装货 → 持货等单 → 追加后回到装货 → 持货等单 → 整车满 → 因满关闭 | PASS | `LOADING CARGO_HOLDING_WAIT LOADING CARGO_HOLDING_WAIT VEHICLE_FULL CLOSED/VEHICLE_FULL` | `LOADING CARGO_HOLDING_WAIT LOADING CARGO_HOLDING_WAIT VEHICLE_FULL CLOSED/VEHICLE_FULL` |
| 从第一张 WAIT 起，之后每一张快照（含 LOADING、FULL 与 CLOSED）都带同一个期限，等于第一次装货落定 + 持货超时（十分钟） | PASS | `2026-09-22T15:31:45.3492212+00:00` | `2026-09-22T15:31:45.3492212+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
