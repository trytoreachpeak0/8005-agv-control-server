# L2 场景证据：vehicle-full-still-appends-before-departure

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T064334370Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `724d6a3d36e010639a86a7d9d9aec0a6be7eb1d9` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T064334370Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 甲、乙两站装完：FRONT 无空仓、REAR 剩两个，车在 11 号站持货等单 | PASS | `CARGO_HOLDING_WAIT, ["FRONT"]` | `AwaitingStationDeparture CARGO_HOLDING_WAIT (LOADED), ["FRONT"]` |
| 需求丁（REAR 3 花篮）只因本车货物装不下：车进入 VEHICLE_FULL，仍停在 11 号站 | PASS | `VEHICLE_FULL, AwaitingStationDeparture` | `AwaitingStationDeparture VEHICLE_FULL` |
| 从判满到需求戊加入、再到加入之后：装货阶段一直是 VEHICLE_FULL，没有退回持货等单再接单 | PASS | `only VEHICLE_FULL` | `seen VEHICLE_FULL; after join AwaitingStationDeparture VEHICLE_FULL` |
| 需求戊（REAR 2 花篮）追加进了这趟旅程：VEHICLE_FULL 在离开最后一个装货站之前仍接追加；追加时 11 号站的停靠还没完成 | PASS | `joined journey:8854d052-a7c8-4e3b-842a-f3b54839d45a / station-11 stop still open` | `joined journey:8854d052-a7c8-4e3b-842a-f3b54839d45a (AwaitingStationDeparture VEHICLE_FULL) / PENDING` |
| 需求戊是被受理的：积压行上有受理时刻 | PASS | `accepted` | `ACCEPTED accepted=2026-09-21 06:45:11.5652421+00:00` |
| 发给车的快照在第一张 VEHICLE_FULL 之后没有再出现 CARGO_HOLDING_WAIT | PASS | `a FULL, then no WAIT` | `1:LOADING 2:LOADING 3:CARGO_HOLDING_WAIT 4:VEHICLE_FULL` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
