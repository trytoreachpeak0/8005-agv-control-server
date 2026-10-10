# L2 场景证据：waiting-station-yield

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160145875Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160145875Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 持单车 AGV-L2-001 装完需求甲（1 花篮）、两侧都没满，在 12 号站上 CARGO_HOLDING_WAIT | PASS | `AwaitingStationDeparture CARGO_HOLDING_WAIT` | `AwaitingStationDeparture CARGO_HOLDING_WAIT` |
| 需求乙由另一台车 AGV-L2-002 受理，它的下一停靠就是持单车所在的 12 号站 | PASS | `AGV-L2-002 AwaitingPickupArrival → 12` | `AGV-L2-002 AwaitingPickupArrival → 12` |
| 另一台车受理之后，持单车装货阶段 CLOSED/WAITING_STATION_YIELD；触发列记的是那台车，时刻不早于受理 | PASS | `CLOSED/WAITING_STATION_YIELD by BROKERX-L2-0002 at or after 2026-10-09T16:02:18.8803615+00:00` | `AwaitingStationDeparture CLOSED/WAITING_STATION_YIELD by 'BROKERX-L2-0002' at 2026-10-09T16:02:18.8803615+00:00` |
| 持单车收到了 CLOSED/WAITING_STATION_YIELD 那张车辆业务状态快照 | PASS | `>= 1` | `1` |
| 让站之后主车离站开向关卡：关卡腿建了单 | PASS | `a gate leg intent` | `W2G-302e4e50-9a23-4c6c-bcc1-896bdb30eaa1-GATE-1 CONFIRMED` |
| 让站之后发的需求戊（后侧，主车后侧全空）不进主车那一趟 | PASS | `judged, and not on journey:302e4e50-9a23-4c6c-bcc1-896bdb30eaa1` | `on journey:0d9b7ce0-98bc-4a17-bab2-ea297f466c84 (AGV-L2-002)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
