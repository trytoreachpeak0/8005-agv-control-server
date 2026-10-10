# L2 场景证据：waiting-station-yield

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T124507768Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `c61009b85933d824289f1a06d4854a8e23651129` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T124507768Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 持单车 AGV-L2-001 装完需求甲（1 花篮）、两侧都没满，在 12 号站上 CARGO_HOLDING_WAIT | PASS | `AwaitingStationDeparture CARGO_HOLDING_WAIT` | `AwaitingStationDeparture CARGO_HOLDING_WAIT` |
| 需求乙由另一台车 AGV-L2-002 受理，它的下一停靠就是持单车所在的 12 号站 | PASS | `AGV-L2-002 AwaitingPickupArrival → 12` | `AGV-L2-002 AwaitingPickupArrival → 12` |
| 另一台车受理之后，持单车装货阶段 CLOSED/WAITING_STATION_YIELD；触发列记的是那台车，时刻不早于受理 | PASS | `CLOSED/WAITING_STATION_YIELD by BROKERX-L2-0002 at or after 2026-09-21T12:45:54.3449257+00:00` | `AwaitingStationDeparture CLOSED/WAITING_STATION_YIELD by 'BROKERX-L2-0002' at 2026-09-21T12:45:54.3449257+00:00` |
| 持单车收到了 CLOSED/WAITING_STATION_YIELD 那张车辆业务状态快照 | PASS | `>= 1` | `1` |
| 让站之后主车离站开向关卡：关卡腿建了单 | PASS | `a gate leg intent` | `W2G-cb005ff7-51dc-40b0-8587-1a9f5164de55-GATE-1 CONFIRMED` |
| 让站之后发的需求戊（后侧，主车后侧全空）不进主车那一趟 | PASS | `judged, and not on journey:cb005ff7-51dc-40b0-8587-1a9f5164de55` | `on journey:d1b894e9-e555-4ba3-adbe-bd9277bc7fb2 (AGV-L2-002)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
