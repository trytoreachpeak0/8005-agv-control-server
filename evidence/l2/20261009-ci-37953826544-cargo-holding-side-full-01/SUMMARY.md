# L2 场景证据：cargo-holding-side-full

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T154441530Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T154441530Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求甲装完、FRONT 已无空仓，但 REAR 还空着：车进入持货等单（CARGO_HOLDING_WAIT），不是 VEHICLE_FULL | PASS | `CARGO_HOLDING_WAIT, started` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, started '2026-10-09 15:45:12.5487717+00:00'` |
| 需求乙追加进了持货等单的这辆车（同一趟旅程） | PASS | `journey:551ee52c-01be-4ee9-a0d9-b3233c039e5a` | `journey:551ee52c-01be-4ee9-a0d9-b3233c039e5a` |
| 需求乙装完、REAR 还有两个空仓：仍是 CARGO_HOLDING_WAIT，起算点没有因为新停靠重置 | PASS | `CARGO_HOLDING_WAIT, started 2026-10-09 15:45:12.5487717+00:00` | `CARGO_HOLDING_WAIT (LOADED), started 2026-10-09 15:45:12.5487717+00:00` |
| 又转了五轮，REAR 仍有空仓且没有「只因本车货物装不下」的候选：状态仍是 CARGO_HOLDING_WAIT，FRONT 单侧满不算整车满 | PASS | `CARGO_HOLDING_WAIT` | `AwaitingStationDeparture CARGO_HOLDING_WAIT full sides ["FRONT"]` |
| 需求丁（3 花篮）只因本车货物占着 REAR 而装不下：车进入 VEHICLE_FULL，满的两侧是 FRONT 与 REAR | PASS | `VEHICLE_FULL, ["FRONT","REAR"]` | `AwaitingStationDeparture VEHICLE_FULL, ["FRONT","REAR"], D backlog SLOT_GROUP_OCCUPIED_BY_OWN_CARGO` |
| 车离开最后一个装货停靠：装货阶段关闭，理由 VEHICLE_FULL | PASS | `CLOSED/VEHICLE_FULL` | `AwaitingGateArrival CLOSED/VEHICLE_FULL` |
| 需求丁没有进这趟旅程，关闭之后它的积压理由是 LOADING_PHASE_CLOSED | PASS | `no journey / LOADING_PHASE_CLOSED` | `no journey / LOADING_PHASE_CLOSED` |
| 车辆业务状态快照的修订号不重号（车载端号同内容不同会当场拆会话） | PASS | `7 distinct` | `7 of 7` |
| 车收到的装货阶段依次是：到站装货 → 持货等单 → 追加后回到装货 → 持货等单 → 整车满 → 因满关闭 | PASS | `LOADING CARGO_HOLDING_WAIT LOADING CARGO_HOLDING_WAIT VEHICLE_FULL CLOSED/VEHICLE_FULL` | `LOADING CARGO_HOLDING_WAIT LOADING CARGO_HOLDING_WAIT VEHICLE_FULL CLOSED/VEHICLE_FULL` |
| 从第一张 WAIT 起，之后每一张快照（含 LOADING、FULL 与 CLOSED）都带同一个期限，等于第一次装货落定 + 持货超时（十分钟） | PASS | `2026-10-09T15:55:12.5487717+00:00` | `2026-10-09T15:55:12.5487717+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
