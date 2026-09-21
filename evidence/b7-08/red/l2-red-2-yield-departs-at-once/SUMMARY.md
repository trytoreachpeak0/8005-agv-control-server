# L2 场景证据：waiting-station-yield-waits-for-door

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T114012527Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `5a772043198a7fdb17dbee626085ef7bae355201` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T114012527Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 主车 AGV-L2-001 接下前侧 4 花篮的需求甲，后侧无空仓：VEHICLE_FULL；到站后装货命令在执行 | PASS | `AwaitingLoadResult VEHICLE_FULL` | `AwaitingLoadResult VEHICLE_FULL` |
| 需求乙由另一台车 AGV-L2-002 受理，它的下一停靠就是持单车所在的 12 号站 | PASS | `AGV-L2-002 AwaitingPickupArrival → 12` | `AGV-L2-002 AwaitingPickupArrival → 12` |
| 另一台车受理之后，持单车装货阶段 CLOSED/WAITING_STATION_YIELD；触发列记的是那台车，时刻不早于受理 | PASS | `CLOSED/WAITING_STATION_YIELD by BROKERX-L2-0002 at or after 2026-09-21T11:41:00.1042148+00:00` | `AwaitingDepartureSafety CLOSED/WAITING_STATION_YIELD by 'BROKERX-L2-0002' at 2026-09-21T11:41:00.1042148+00:00` |
| 持单车收到了 CLOSED/WAITING_STATION_YIELD 那张车辆业务状态快照 | PASS | `>= 1` | `1` |
| 让站已触发、门开着、装货未落定，服务端又转了 4 轮：没有离站核验，关卡腿没有建单 | FAIL | `0 departure checks, no gate intent, WAITING_STATION_YIELD` | `1 departure checks, no gate intent, AwaitingDepartureSafety CLOSED/WAITING_STATION_YIELD` |
| 门关上之后主车才离站：放行离站的那一次核验请求与关卡腿的订单意图，都晚于服务端收到「门已关」 | PASS | `door closed < departure check, door closed < gate order intent` | `door closed 2026-09-21T11:41:04.9053892+00:00; departure check 2026-09-21T11:41:05.7483112+00:00; gate order intent 2026-09-21T11:41:05.8499755+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
