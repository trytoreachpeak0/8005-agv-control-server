# L2 场景证据：vehicle-full-still-appends-before-departure

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T155159314Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T155159314Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 甲、乙两站装完：FRONT 无空仓、REAR 剩两个，车在 11 号站持货等单 | PASS | `CARGO_HOLDING_WAIT, ["FRONT"]` | `AwaitingStationDeparture CARGO_HOLDING_WAIT (LOADED), ["FRONT"]` |
| 需求丁（REAR 3 花篮）只因本车货物装不下：车进入 VEHICLE_FULL，仍停在 11 号站 | PASS | `VEHICLE_FULL, AwaitingStationDeparture` | `AwaitingStationDeparture VEHICLE_FULL` |
| 需求戊（REAR 2 花篮）追加进了这趟旅程：VEHICLE_FULL 在离开最后一个装货站之前仍接追加；追加时 11 号站的停靠还没完成 | PASS | `joined journey:be540b6b-ec36-4336-9fe1-d674819f08e7 / station-11 stop still open` | `joined journey:be540b6b-ec36-4336-9fe1-d674819f08e7 (AwaitingStationDeparture VEHICLE_FULL) / PENDING` |
| 需求戊是被受理的：积压行上有受理时刻 | PASS | `accepted` | `ACCEPTED accepted=2026-10-09 15:53:34.0364201+00:00` |
| 从判满到需求戊加入、再到加入之后又转了三轮：装货阶段一直是 VEHICLE_FULL，没有退回持货等单或装货 | PASS | `only VEHICLE_FULL` | `seen VEHICLE_FULL; three rounds after join AwaitingStationDeparture VEHICLE_FULL` |
| 需求戊装上了车，车离开它那个取货停靠（最后一个装货停靠）时以 VEHICLE_FULL 关闭 | PASS | `E LOADED / CLOSED/VEHICLE_FULL` | `E LOADED / AwaitingGateArrival CLOSED/VEHICLE_FULL` |
| 发给车的快照在第一张 VEHICLE_FULL 之后没有再出现 CARGO_HOLDING_WAIT 或 LOADING | PASS | `a FULL, then neither WAIT nor LOADING` | `1:LOADING 2:LOADING 3:CARGO_HOLDING_WAIT 4:VEHICLE_FULL 5:VEHICLE_FULL 6:CLOSED/VEHICLE_FULL` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
