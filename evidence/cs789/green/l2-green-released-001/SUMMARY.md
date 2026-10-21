# L2 场景证据：in-transit-door-unknown-stopped-released

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T062907989Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `ec320e95b76b8640ce7c3815a78b6e9891916ac7` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T062907989Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 读不到之后不早于 3 秒、不晚于 6 秒才发 Hold；Hold 恰好一条，打在本车本单上 | PASS | `1 / W2G-88590789-6fac-428b-b0da-d1b9001bfa5a-PICKUP-1 / 3-6 s` | `1 / W2G-88590789-6fac-428b-b0da-d1b9001bfa5a-PICKUP-1 / 3.8 s` |
| 急停恰好一条，打在这台车上、在 Hold 之后；故障事实是门锁症状 | PASS | `1 / vehicle:BROKERX-L2-0001 / hold first / VEHICLE_DOOR_NOT_PROVEN_LOCKED` | `1 / vehicle:BROKERX-L2-0001 / VEHICLE_DOOR_NOT_PROVEN_LOCKED` |
| 门锁仍读不到时，急停不解除 | PASS | `0` | `0` |
| 读回锁闭之后不早于 10 秒，服务端自己解除一次，原因 EMERGENCY_DOOR_READING_RESTORED，挂在这一代故障下 | PASS | `1 / EMERGENCY_DOOR_READING_RESTORED / >= 10 s / same generation` | `1 / EMERGENCY_DOOR_READING_RESTORED / 10.8 s / 1 vs 1` |
| 解除之后：没有 CONTINUE（审计与 RIoT 两侧）、仍只 1 次急停、故障仍在效、旅程码仍是门锁症状 | PASS | `0 / 0 / 1 / fault in effect` | `0 / 0 / 1 / SuspectedBlocked` |
| 继续原单：入口 200 Resumed，CONTINUE 恰好一次打在这一张 orderId 上，故障清除 | PASS | `200 / Resumed / 1 -> ORDER-000001 / None` | `200 / Resumed / 1 -> ORDER-000001 / None` |
| 续行之后车在走、门锁着：不再急停、不再按住，全程没有 Cancel | PASS | `1 / 1 / 0` | `1 / 1 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
