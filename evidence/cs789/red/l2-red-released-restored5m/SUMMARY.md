# L2 场景证据：in-transit-door-unknown-stopped-released

结论：**FAIL**

失败原因：Timed out after 45s waiting for: the server released the emergency stop on its own. Last observed: 0

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T063028371Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `77fcd70ba841bb73e5d798fd5e4721d3cde0dc4c` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T063028371Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 读不到之后不早于 3 秒、不晚于 6 秒才发 Hold；Hold 恰好一条，打在本车本单上 | PASS | `1 / W2G-26e3c1fe-a7f9-4831-83fd-5f36464a4a21-PICKUP-1 / 3-6 s` | `1 / W2G-26e3c1fe-a7f9-4831-83fd-5f36464a4a21-PICKUP-1 / 3.1 s` |
| 急停恰好一条，打在这台车上、在 Hold 之后；故障事实是门锁症状 | PASS | `1 / vehicle:BROKERX-L2-0001 / hold first / VEHICLE_DOOR_NOT_PROVEN_LOCKED` | `1 / vehicle:BROKERX-L2-0001 / VEHICLE_DOOR_NOT_PROVEN_LOCKED` |
| 门锁仍读不到时，急停不解除 | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
