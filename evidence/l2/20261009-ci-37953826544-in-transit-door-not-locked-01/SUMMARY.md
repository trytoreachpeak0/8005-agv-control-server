# L2 场景证据：in-transit-door-not-locked

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162205012Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162205012Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车在正常行驶、门锁着时，不发 Hold、不发急停 | PASS | `0 / 0` | `0 / 0` |
| Hold 恰好一条，打在本车、本单上，挂在这一代故障下 | PASS | `1 / AGV-L2-001 / W2G-5390d657-ce8f-4cad-b462-0efcfb63702a-PICKUP-1` | `1 / AGV-L2-001 / W2G-5390d657-ce8f-4cad-b462-0efcfb63702a-PICKUP-1` |
| RIoT 侧收到的是 CMD_ORDER_HELD，打在这一张 orderId 上 | PASS | `CMD_ORDER_HELD -> ORDER-000001` | `CMD_ORDER_HELD -> ORDER-000001` |
| 急停恰好一条，打在这台车上，而且在 Hold 之后 | PASS | `1 / AGV-L2-001 / vehicle:BROKERX-L2-0001 / hold first` | `1 / AGV-L2-001 / vehicle:BROKERX-L2-0001 / hold 2026-10-09 16:22:32.8573879+00:00 trigger 2026-10-09 16:22:33.0128789+00:00` |
| 故障事实记的是门锁症状，旅程码也是它 | PASS | `VEHICLE_DOOR_NOT_PROVEN_LOCKED / VEHICLE_DOOR_NOT_PROVEN_LOCKED` | `VEHICLE_DOOR_NOT_PROVEN_LOCKED / VEHICLE_DOOR_NOT_PROVEN_LOCKED` |
| 按不住时急停只发一次：再跑几轮，审计表与 RIoT 侧都仍是一次 | PASS | `1 / 1` | `1 / 1` |
| 门还报没锁时，急停不解除 | PASS | `0` | `0` |
| 门锁恢复后自动解除一次，原因记的是门锁原因消除，本单的 Hold 已回查确认 | PASS | `1 / EMERGENCY_DOOR_CAUSE_REMOVED / Confirmed` | `1 / EMERGENCY_DOOR_CAUSE_REMOVED / Confirmed` |
| 解除之后单仍停着：没有任何 CONTINUE，审计表与 RIoT 侧都没有 | PASS | `0 / 0` | `0 / 0` |
| 解除之后不再急停，故障仍在效（等人继续原单） | PASS | `1 trigger / fault in effect` | `1 trigger / SuspectedBlocked` |
| 全程没有任何 Cancel 代替停车（REQ-0246） | PASS | `0 / 0` | `0 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
