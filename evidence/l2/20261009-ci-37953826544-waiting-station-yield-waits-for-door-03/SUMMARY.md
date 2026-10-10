# L2 场景证据：waiting-station-yield-waits-for-door

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160244566Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160244566Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 主车 AGV-L2-001 接下前侧 4 花篮的需求甲，后侧无空仓：VEHICLE_FULL；到站后装货命令在执行 | PASS | `AwaitingLoadResult VEHICLE_FULL` | `AwaitingLoadResult VEHICLE_FULL` |
| 需求乙由另一台车 AGV-L2-002 受理，它的下一停靠就是持单车所在的 12 号站 | PASS | `AGV-L2-002 AwaitingPickupArrival → 12` | `AGV-L2-002 AwaitingPickupArrival → 12` |
| 另一台车受理之后，持单车装货阶段 CLOSED/WAITING_STATION_YIELD；触发列记的是那台车，时刻不早于受理 | PASS | `CLOSED/WAITING_STATION_YIELD by BROKERX-L2-0002 at or after 2026-10-09T16:03:16.9649628+00:00` | `AwaitingLoadResult CLOSED/WAITING_STATION_YIELD by 'BROKERX-L2-0002' at 2026-10-09T16:03:16.9649628+00:00` |
| 持单车收到了 CLOSED/WAITING_STATION_YIELD 那张车辆业务状态快照 | PASS | `>= 1` | `1` |
| 让站已触发、门开着、装货未落定，服务端又转了 4 轮：没有离站核验，关卡腿没有建单 | PASS | `0 departure checks, no gate intent, WAITING_STATION_YIELD` | `0 departure checks, no gate intent, AwaitingLoadResult CLOSED/WAITING_STATION_YIELD` |
| 装货已落定、门仍开着，站点等待早已过去，服务端又转了几轮：主车没有离站，关卡腿没有建单 | PASS | `no gate intent, WAITING_STATION_YIELD` | `no gate intent, AwaitingLoadResult CLOSED/WAITING_STATION_YIELD 'ONBOARD_SESSION_NOT_READY'` |
| 门关上之后主车才离站：放行离站的那一次核验请求与关卡腿的订单意图，都晚于服务端收到「门已关」 | PASS | `door closed < departure check, door closed < gate order intent` | `door closed 2026-10-09T16:03:38.8258555+00:00; departure check 2026-10-09T16:03:50.7210202+00:00; gate order intent 2026-10-09T16:03:50.8015199+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
