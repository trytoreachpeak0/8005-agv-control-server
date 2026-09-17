# L2 场景证据：session-established-while-moving

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260916T134917567Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `15ac3a95ec7b4dd2d9692b6c302945f4d1590e91` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260916T134917567Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 会话建立时车载端报告车辆仍在运动 | PASS | `vehicleStopped=False @ safetyStateVersion=1` | `vehicleStopped=False @ safetyStateVersion=1` |
| 车在动不影响会话就绪（就绪只看 departureSafe） | PASS | `Ready` | `Ready` |
| 车在动时需求判 ONBOARD_DEPARTURE_UNSAFE | PASS | `ONBOARD_DEPARTURE_UNSAFE` | `ONBOARD_DEPARTURE_UNSAFE` |
| 被拒的需求没有 journey，也没有建单 | PASS | `(no runtime, no intent)` | `runtime=(none) intent=(none)` |
| 车停稳后同一条需求被受理并派车 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 同一条 backlog 记录翻成 ACCEPTED | PASS | `ACCEPTED` | `ACCEPTED` |
| 推进是被车载端发出的 SafetyStateChanged 推动的 | PASS | `>= 1` | `1` |
| 车载端仍报运动时，服务端不采信 RIoT 的到站 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 车载端报停稳后到站被采信，进入 AwaitingSublot | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 全程只建了一条 RIoT 单（被拒期间没有派过车） | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
