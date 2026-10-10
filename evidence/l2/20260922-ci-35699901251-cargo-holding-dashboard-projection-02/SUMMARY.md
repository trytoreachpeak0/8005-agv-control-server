# L2 场景证据：cargo-holding-dashboard-projection

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T074015622Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T074015622Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 持单车 AGV-L2-001 装完需求甲（1 花篮）、两侧都没满，在 12 号站上 CARGO_HOLDING_WAIT | PASS | `AwaitingStationDeparture CARGO_HOLDING_WAIT` | `AwaitingStationDeparture CARGO_HOLDING_WAIT` |
| 端点列出主车持货等单，期限等于库里的起算点加服务端配置的 10 分钟，剩余时间在 (0, 600] 秒 | PASS | `CARGO_HOLDING_WAIT deadline 2026-09-22T07:50:38.0447392+00:00, remaining in (0, 600]` | `CARGO_HOLDING_WAIT deadline 2026-09-22T07:50:38.0447392+00:00 remaining 599 s yieldedTo ''` |
| 过一会儿再读：剩余时间比第一次少，期限一秒不差 | PASS | `remaining < 599, deadline 2026-09-22T07:50:38.0447392+00:00` | `CARGO_HOLDING_WAIT deadline 2026-09-22T07:50:38.0447392+00:00 remaining 598 s yieldedTo ''` |
| 看板那一页的持货等单卡片渲染出主车这一行：持货等单、剩余时间 | PASS | `持货等单 … N 分 N 秒` | `AGV-L2-001  N1-3_N1-7  持货等单  2026-09-22 15:50:38  9 分 58 秒  前侧：未满；后侧：未满    L2-CH-A-20260922T074015622Z\|WIRE_TO_GATE：已装` |
| 需求乙由另一台车 AGV-L2-002 受理，它的下一停靠就是持单车所在的 12 号站 | PASS | `AGV-L2-002 AwaitingPickupArrival → 12` | `AGV-L2-002 AwaitingPickupArrival → 12` |
| 另一台车受理之后，持单车装货阶段 CLOSED/WAITING_STATION_YIELD；触发列记的是那台车，时刻不早于受理 | PASS | `CLOSED/WAITING_STATION_YIELD by BROKERX-L2-0002 at or after 2026-09-22T07:40:40.3202692+00:00` | `AwaitingStationDeparture CLOSED/WAITING_STATION_YIELD by 'BROKERX-L2-0002' at 2026-09-22T07:40:40.3202692+00:00` |
| 持单车收到了 CLOSED/WAITING_STATION_YIELD 那张车辆业务状态快照 | PASS | `>= 1` | `1` |
| 端点里主车那一行 CLOSED/WAITING_STATION_YIELD，触发的车是另一台车 BROKERX-L2-0002，不再倒计时 | PASS | `CLOSED/WAITING_STATION_YIELD yieldedTo 'BROKERX-L2-0002' remaining (null)` | `CLOSED/WAITING_STATION_YIELD deadline 2026-09-22T07:50:38.0447392+00:00 remaining  s yieldedTo 'BROKERX-L2-0002'` |
| 看板那一页主车那一行写着已结束与让站原因、触发的车 | PASS | `已结束 … 另一辆车以本站为下一停靠，本车结束等单（BROKERX-L2-0002）` | `AGV-L2-001  N1-3_N1-7  已结束  2026-09-22 15:50:38    前侧：已满；后侧：未满  另一辆车以本站为下一停靠，本车结束等单（BROKERX-L2-0002）  L2-CH-A-20260922T074015622Z\|WIRE_TO_GATE：已装` |
| 放行离站核验之后主车离站（库里取货停靠 COMPLETED），端点与看板那一页都不再列这台车 | PASS | `check answered, PICKUP COMPLETED, not listed` | `check answered, PICKUP COMPLETED, endpoint (not listed), page (no row)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
