# L2 场景证据：in-transit-door-not-locked

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260928T093445332Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `a820183092dd1ec7f2810f50be8ff0b0dd44a39c` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260928T093445332Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车在正常行驶、门锁着时，不发 Hold、不发急停 | PASS | `0 / 0` | `0 / 0` |
| Hold 恰好一条，打在本车、本单上，挂在这一代故障下 | PASS | `1 / AGV-L2-001 / W2G-95f294aa-b3fb-4d5d-ad24-da43cb6c9881-PICKUP-1` | `1 / AGV-L2-001 / W2G-95f294aa-b3fb-4d5d-ad24-da43cb6c9881-PICKUP-1` |
| RIoT 侧收到的是 CMD_ORDER_HELD，打在这一张 orderId 上 | PASS | `CMD_ORDER_HELD -> ORDER-000001` | `CMD_ORDER_HELD -> ORDER-000001` |
| 急停恰好一条，打在这台车上，而且在 Hold 之后 | PASS | `1 / AGV-L2-001 / vehicle:BROKERX-L2-0001 / hold first` | `1 / AGV-L2-001 / vehicle:BROKERX-L2-0001 / hold 2026-09-28 09:35:15.1806583+00:00 trigger 2026-09-28 09:35:15.2848058+00:00` |
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
