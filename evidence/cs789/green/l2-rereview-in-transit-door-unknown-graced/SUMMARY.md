# L2 场景证据：in-transit-door-unknown-graced

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T092534426Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3e5f0b32218e41fcad831d05ffedaa59b05814ef` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T092534426Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车在正常行驶、门锁着时，不发 Hold、不发急停、没有故障 | PASS | `hold 0/0 trigger 0/0 fault 0 cancel 0` | `hold 0/0 trigger 0/0 fault 0 cancel 0` |
| 读不到的那 1 秒多里（宽限 3 秒之内）：不发 Hold、不发急停、没有故障 | PASS | `gap < 2.5 s / hold 0/0 trigger 0/0 fault 0` | `gap 1.3 s / hold 0/0 trigger 0/0 fault 0 cancel 0` |
| 读回锁闭之后再转 6 轮：审计表与 RIoT 两侧都没有 Hold、急停、Cancel，也没有故障事实 | PASS | `hold 0/0 trigger 0/0 fault 0 cancel 0` | `hold 0/0 trigger 0/0 fault 0 cancel 0` |
| 旅程码不是门锁症状（车照常在路上） | PASS | `not VEHICLE_DOOR_NOT_PROVEN_LOCKED` | `ONBOARD_SESSION_NOT_READY` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
